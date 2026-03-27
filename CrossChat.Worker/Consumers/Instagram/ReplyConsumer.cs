using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using Grpc.Core;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Instagram;

public static class MediaMessageStorage
{
	public static ConcurrentDictionary<string, List<MediaDataEntry>> Storage = new();
}

public class ReplyConsumer : IConsumer<ProcessDialogReply>
{
	private readonly ILogger<ReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IInstagramService _instaService;
	private readonly IAiService _aiService;

	// Статический лимитер (один на всё приложение)
	// 2. СОЗДАЕМ ЛИМИТЕР (Static - один на все потоки приложения)
	// Настройка: 15 запросов в 1 минуту (Безопасно для Free Tier)
	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 20,                     // Сколько разрешаем (20 шт)
		Window = TimeSpan.FromMinutes(1),     // За какое время (1 мин)
		QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
		QueueLimit = 0
	});

	IDatabase _redis;
	// Сюда потом внедришь свои сервисы: IInstagramService, IAiService
	public ReplyConsumer(ILogger<ReplyConsumer> logger, AppDbContext db, IInstagramService instaService,
		IAiService aiService, IConnectionMultiplexer redisMux)
	{
		_logger = logger;
		_db = db;
		_instaService = instaService;
		_aiService = aiService;
		_redis = redisMux.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
		var messageId = context.Message.ReplyId; // Передай сюда уникальный ID сообщения
		var processingKey = $"processed:{messageId}";

		// 1. Проверяем, не было ли сообщение уже обработано УСПЕШНО ранее
		if (await _redis.KeyExistsAsync(processingKey))
		{
			_logger.LogInformation("Сообщение уже обработано успешно, игнорируем дубль.");
			return;
		}

		// Пытаемся получить разрешение на выполнение
		using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, context.CancellationToken);

		if (!lease.IsAcquired)
		{
			// Лимит исчерпан -> бросаем исключение, чтобы сработал Redelivery (повтор через минуту)
			throw new Exception("Rate limit exceeded (Gemini). Triggering Redelivery.");
		}

		var senderId = context.Message.SenderId;       // Клиент (кто написал)
		var businessAccountId = context.Message.RecipientId; // Бот (кому написали)

		_logger.LogInformation($"[Reply] 🚀 Обработка диалога. BusinessID: {businessAccountId}, SenderID: {senderId}");

		// 2. Ищем настройки владельца бота в БД
		// Нам нужно найти того юзера, у которого InstagramBusinessId совпадает с RecipientId
		var settings = await _db.InstagramSettings
			.AsNoTracking()
			.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);

		//_logger.LogInformation($"[Reply] Проверка настроек: BotID={businessAccountId}, Active={settings.IsActive}, Prompt={settings.SystemPrompt}");

		if (settings == null)
		{
			_logger.LogWarning($"[Reply] ❌ Настройки для BusinessID {businessAccountId} не найдены в БД. Игнорируем.");
			return;
		}

		if (!settings.IsActive)
		{
			_logger.LogInformation($"[Reply] ⏸ Бот выключен пользователем. Пропускаем.");
			return;
		}

		if (!settings.IsDirectEnabled)
		{
			_logger.LogInformation($"[Reply] ⏸ У бота выключены ответы на сообщения. Пропускаем.");
			return;
		}

		var accessInstaToken = settings.AccessToken;

		if (string.IsNullOrEmpty(accessInstaToken))
		{
			_logger.LogError($"[Reply] ❌ Токен отсутствует для BusinessID {businessAccountId}.");
			return;
		}

		try
		{
			// 3. Получаем историю переписки (используя токен юзера)
			var messages = await _instaService.GetHistoryAsync(senderId, accessInstaToken, 10);

			if (messages == null || messages.Count == 0) return;

			var lastMsg = messages[0];
			string lastSenderId = lastMsg.From.Id;
			if (lastSenderId == businessAccountId)
			{
				await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
				return;
			}

			int unreadCount = 0;
			foreach (var msg in messages) { if (msg.From.Id != businessAccountId) unreadCount++; else break; }

			var chatHistory = new List<AiRequest>();
			var unreadUserMessageIds = new List<string>(); // Для реакций

			// Идем по списку с конца (от старых) к началу (к новым), чтобы сохранить хронологию
			for (int i = messages.Count - 1; i >= 0; i--)
			{
				var msg = messages[i];

				// 1. Получаем текстовое содержание (с учетом кэша, фото, видео)
				string content = await ResolveMessageContentAsync(msg);
				_logger.LogInformation($"[CHAT MSG] {content}");
				if (content is null)
				{
					_logger.LogInformation("Медиа еще обрабатываются, Snooze...");
					// Ставим себя в очередь снова через 10 секунд
					await context.SchedulePublish(TimeSpan.FromSeconds(60), context.Message);
					return;
				}

				// 2. Определяем роль для AI (model - это бот, user - это пользователь)
				string role = msg.From.Id == businessAccountId ? "model" : "user";

				// 3. Добавляем в историю в формате объектов
				chatHistory.Add(new AiRequest
				{
					Role = role,
					Text = string.IsNullOrEmpty(content) ? "[Empty message]" : content
				});

				// 4. Логика для Реакций:
				// Проверяем, является ли сообщение непрочитанным (по индексу unreadCount) и от пользователя
				// (unreadCount вычисляется перед этим циклом, как в прошлом коде)
				bool isUnread = i < unreadCount;
				if (isUnread && role == "user")
				{
					unreadUserMessageIds.Add(msg.Id);
				}
			}

			var random = new Random();

			// Если есть непрочитанные сообщения от юзера и выпал шанс (например > 50 из 100)
			if (unreadUserMessageIds.Count > 0 && random.Next(1, 101) > 50)
			{
				// Выбираем случайное сообщение из списка непрочитанных
				string targetMessageId = unreadUserMessageIds[random.Next(unreadUserMessageIds.Count)];

				// Отправляем реакцию (без await, чтобы не задерживать процесс, или с await для надежности)
				await _instaService.SendReactionAsync(senderId, targetMessageId, accessInstaToken); // Например "love" или рандом

				// Небольшая пауза для реалистичности перед тем как "печатать"
				await Task.Delay(1500);
			}

			await _instaService.SetTypingStatusAsync(senderId, accessInstaToken);

			try
			{
				// 4. Отправляем в ИИ (через твой gRPC сервис)
				var systemPrompt = settings.SystemPrompt ?? "Ты полезный помощник.";

				string userContextInfo = await _instaService.GetUserContextForAiAsync(senderId, accessInstaToken);
				systemPrompt += "\n\nKeep this information in mind when responding. For example, whether you are mutual subscribers. If not, ask him to subscribe.\n"
					+ userContextInfo;

				//_logger.LogInformation($"Системный промпт {systemPrompt}");
				var aiResponse = await _aiService.GetAnswerAsync(systemPrompt, chatHistory, null);

				if (string.IsNullOrWhiteSpace(aiResponse))
				{
					_logger.LogError("[Reply] ИИ вернул пустой ответ.");

					string retryKey = $"retry_ai_{messageId}"; // ID вашего сообщения
					long retryCount = await _redis.StringIncrementAsync(retryKey);

					if (retryCount == 1)
					{
						// Устанавливаем время жизни ключа, чтобы он не висел вечно
						await _redis.KeyExpireAsync(retryKey, TimeSpan.FromMinutes(10));
					}

					// 2. Лимит попыток (например, 3 раза)
					if (retryCount <= 3)
					{
						_logger.LogInformation($"[Reply] Попытка {retryCount}/3. Отправим запрос в очередь через {60*retryCount}сек...");
						await context.SchedulePublish(TimeSpan.FromSeconds(60*retryCount), context.Message);
						return; // Выходим
					}

					return;
				}

				// 5. Отправляем ответ в Инстаграм
				await SendLongMessageAsHumanAsync(senderId, aiResponse, accessInstaToken);

				// Если мы это сообщение УЖЕ обработали — выходим
				await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);

				_logger.LogInformation($"[Reply] ✅ Ответ успешно отправлен пользователю {senderId}");
			}
			catch (RpcException ex) when (ex.StatusCode == StatusCode.Internal && ex.Message.Contains("blocked"))
			{
				// ОШИБКА БЕЗОПАСНОСТИ (Google Policy) - ПОВТОР НЕ ПОМОЖЕТ
				_logger.LogWarning("Gemini заблокировал контент. Прекращаем обработку.");
			}
			catch (Exception ex)
			{
				// ТЕХНИЧЕСКАЯ ОШИБКА (Сеть, API упало) - ПОВТОР ПОМОЖЕТ
				_logger.LogError(ex, "Техническая ошибка. RabbitMQ сделает Retry.");
				throw; // Вот здесь мы кидаем throw, чтобы сработал твой UseMessageRetry
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"[Reply] 💥 Ошибка при обработке диалога {senderId}");
			// Здесь можно решить: бросать исключение (чтобы повторить попытку) или нет.
			// Если ошибка в логике (например, ИИ упал) - лучше повторить.
			throw;
		}
	}

	public async Task SendLongMessageAsHumanAsync(string userId, string fullText, string token)
	{
		// 1. Разбиваем текст на части (например, по ~200 символов или по предложениям)
		var chunks = SplitMessageIntoHumanChunks(fullText, 100);

		for (int i = 0; i < chunks.Count; i++)
		{
			await _instaService.SetTypingStatusAsync(userId, token);

			var chunk = chunks[i];

			// 3. Рассчитываем паузу для ТЕКУЩЕГО куска
			// Чем короче кусок, тем быстрее мы его "печатаем"
			int typingTime = Math.Clamp(chunk.Length * 90, 2000, 6000);
			await Task.Delay(typingTime);

			await _instaService.SendMessageAsync(userId, chunk, token);

			// 5. Маленькая пауза между отправкой и началом печати следующего (как будто человек нажал Enter)
			if (i < chunks.Count - 1)
			{
				await Task.Delay(Random.Shared.Next(1000, 2000));
			}
		}

		if (chunks.Count == 1)
		{
			var random = new Random();

			// Если выпадает число от 1 до 3 (из 10), то отправляем стикер. Шанс 30%.
			if (random.Next(1, 11) <= 3)
			{
				await _instaService.SetTypingStatusAsync(userId, token);

				// Небольшая задержка перед стикером, чтобы выглядело естественно (1-3 сек)
				//await Task.Delay(random.Next(1000, 3000));

				//string stickerToSend;

				//if (random.Next(1, 101) > 10)
				//{
				//	stickerToSend = "like_heart";
				//}
				//else
				//{
				//	// Берем случайный URL из нашей коллекции
				//	int index = random.Next(StickerCollection.Urls.Count);
				//	stickerToSend = StickerCollection.Urls[index];
				//}

				//await SendSticker(userId, stickerToSend);
			}
		}
	}

	private List<string> SplitMessageIntoHumanChunks(string text, int maxChunkLength)
	{
		var chunks = new List<string>();
		if (string.IsNullOrEmpty(text)) return chunks;

		// 1. Сначала разбиваем по переносам строк (абзацам)
		var paragraphs = text.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);

		foreach (var paragraph in paragraphs)
		{
			// Если абзац короткий, добавляем его как есть
			if (paragraph.Length <= maxChunkLength)
			{
				chunks.Add(paragraph.Trim());
				continue;
			}

			// 2. Если абзац длинный, бьем его на предложения
			// Используем регулярку, чтобы оставить знаки препинания (.!?) на месте
			var sentences = System.Text.RegularExpressions.Regex.Split(paragraph, @"(?<=[.!?])\s+");

			var currentChunk = "";

			foreach (var sentence in sentences)
			{
				// Если текущий кусок + новое предложение влезают в лимит — склеиваем
				if (currentChunk.Length + sentence.Length <= maxChunkLength)
				{
					currentChunk += (currentChunk.Length > 0 ? " " : "") + sentence;
				}
				else
				{
					// Если не влезают — сохраняем текущий кусок и начинаем новый
					if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
					currentChunk = sentence;
				}
			}

			// Добавляем хвостик
			if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
		}

		return chunks;
	}

	private async Task<string> ResolveMessageContentAsync(MessageItem msg)
	{
		if (!string.IsNullOrEmpty(msg.Text)) return msg.Text;

		if (MediaMessageStorage.Storage.TryGetValue(msg.Id, out var mediaList))
		{
			lock (mediaList)
			{
				// Если хоть одно медиа еще не обработано - возвращаем null (сигнал для Snooze)
				if (mediaList.Any(m => !m.IsProcessed)) return null;

				// Если все обработаны - собираем все AiResult через пробел
				return string.Join(" ", mediaList.Select(m => m.AiResult));
			}
		}

		if (msg.IsUnsupported)
		{
			return "[The user sent a sticker]";
		}

		return "[Empty message]";
	}
}