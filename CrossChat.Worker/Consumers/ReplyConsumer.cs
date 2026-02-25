using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers;

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

	// Сюда потом внедришь свои сервисы: IInstagramService, IAiService
	public ReplyConsumer(ILogger<ReplyConsumer> logger, AppDbContext db, IInstagramService instaService,
		IAiService aiService)
	{
		_logger = logger;
		_db = db;
		_instaService = instaService;
		_aiService = aiService;
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
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
			.AsNoTracking() // Читаем без отслеживания для скорости
			.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);

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

		var accessInstaToken = settings.AccessToken;

		if (string.IsNullOrEmpty(accessInstaToken))
		{
			_logger.LogError($"[Reply] ❌ Токен отсутствует для BusinessID {businessAccountId}.");
			return;
		}

		try
		{
			// 3. Получаем историю переписки (используя токен юзера)
			// Реализуешь получение истории в InstagramService позже
			var messages = await _instaService.GetHistoryAsync(senderId, accessInstaToken);

			// 4. Отправляем в ИИ (через твой gRPC сервис)
			// Берем системный промпт из настроек
			var systemPrompt = settings.SystemPrompt ?? "Ты полезный помощник.";

			if (messages == null || messages.Count == 0) return;

			var lastMsg = messages[0];
			string lastSenderId = lastMsg.From.Id;
			if (lastSenderId == businessAccountId) return;

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

				// 2. Определяем роль для AI (model - это бот, user - это пользователь)
				string role = (msg.From.Id == businessAccountId) ? "model" : "user";

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
				var aiResponse = await _aiService.GetAnswerAsync(systemPrompt, chatHistory, null);

				if (string.IsNullOrWhiteSpace(aiResponse))
				{
					_logger.LogError("[Reply] ИИ вернул пустой ответ.");
					return;
				}

				// 5. Отправляем ответ в Инстаграм
				await SendLongMessageAsHumanAsync(senderId, aiResponse, accessInstaToken);
			}
			catch (Exception ex)
			{
				_logger.LogError($"Ошибка отправки ответа пользаку {senderId} в инсте: {ex.Message}");
				return;
			}

			_logger.LogInformation($"[Reply] ✅ Ответ успешно отправлен пользователю {senderId}");
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
			int typingTime = Math.Clamp(chunk.Length * 70, 1500, 6000);
			await Task.Delay(typingTime);

			await _instaService.SendMessageAsync(userId, chunk, token);

			// 5. Маленькая пауза между отправкой и началом печати следующего (как будто человек нажал Enter)
			if (i < chunks.Count - 1)
			{
				await Task.Delay(Random.Shared.Next(500, 2000));
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
				if ((currentChunk.Length + sentence.Length) <= maxChunkLength)
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
		if (!string.IsNullOrEmpty(msg.Text))
		{
			return msg.Text;
		}

		return "[Empty message]";
	}
}