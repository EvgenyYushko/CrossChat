using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Exceptions;
using Grpc.Core;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

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
	private readonly IInstagramConsole _console;
	private readonly IInboxService _inboxService;

	// Статический лимитер (один на всё приложение)
	// 2. СОЗДАЕМ ЛИМИТЕР (Static - один на все потоки приложения)
	// Настройка: 15 запросов в 1 минуту (Безопасно для Free Tier)
	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 20,                     // Сколько разрешаем (20 шт)
		Window = TimeSpan.FromMinutes(1),     // За какое время (1 мин)
		QueueProcessingOrder = QueueProcessingOrder.OldestFirst, // Кто первый встал, тот первый получит разрешение, когда оно освободится
		QueueLimit = 0
	});

	IDatabase _redis;
	// Сюда потом внедришь свои сервисы: IInstagramService, IAiService
	public ReplyConsumer(ILogger<ReplyConsumer> logger, AppDbContext db, IInstagramService instaService,
		IAiService aiService, IConnectionMultiplexer redisMux, IInstagramConsole console, IInboxService inboxService)
	{
		_logger = logger;
		_db = db;
		_instaService = instaService;
		_aiService = aiService;
		_console = console;
		_inboxService = inboxService;
		_redis = redisMux.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
		var messageId = context.Message.ReplyId;
		var processingKey = $"processed:{messageId}";

		// 1. Проверяем, не было ли сообщение уже обработано УСПЕШНО ранее
		if (await _redis.KeyExistsAsync(processingKey))
		{
			_logger.LogInformation("Сообщение уже обработано успешно, игнорируем дубль.");
			return;
		}

		var senderId = context.Message.SenderId;
		var businessAccountId = context.Message.RecipientId;

		var targetTimeKey = $"debounce:target_time:ig:{senderId}:{businessAccountId}";
		var activeTimerKey = $"debounce:timer_active:ig:{senderId}:{businessAccountId}";

		// ПРОВЕРКА ДЕБАУНСА: продолжал ли пользователь писать?
		var storedTicks = await _redis.StringGetAsync(targetTimeKey);
		if (storedTicks.HasValue && long.TryParse(storedTicks, out long ticks))
		{
			var targetTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
			var remaining = targetTimeUtc - DateTime.UtcNow;

			if (remaining > TimeSpan.FromSeconds(2))
			{
				_logger.LogInformation($"[Instagram Debounce] ⏳ Пользователь {senderId} еще пишет! Откладываем ответ еще на {remaining.TotalSeconds:F0} сек...");
				await context.SchedulePublish(remaining, context.Message);
				return; // НЕ ОТВЕЧАЕМ СЕЙЧАС!
			}
		}

		// Время тишины вышло — сбрасываем ключи дебаунса
		await _redis.KeyDeleteAsync(targetTimeKey);
		await _redis.KeyDeleteAsync(activeTimerKey);

		// =========================================================================
		// 🚀 ЗАЩИТА ОТ ДВОЙНЫХ ОТВЕТОВ: Блокировка диалога (1 поток генерации на юзера)
		// =========================================================================
		var dialogBusyKey = $"lock:dialog_busy:{businessAccountId}:{senderId}";

		// Если диалог прямо сейчас уже обрабатывается другим потоком — откладываем повтор на 10 сек
		if (await _redis.KeyExistsAsync(dialogBusyKey))
		{
			_logger.LogInformation("[Reply] ⏳ Диалог {SenderId} уже генерирует ответ в другом потоке. Откладываем...", senderId);
			await context.SchedulePublish(TimeSpan.FromSeconds(10), context.Message);
			return;
		}

		// Захватываем замок на 90 секунд (с запасом на время ответа нейросети)
		await _redis.StringSetAsync(dialogBusyKey, "busy", TimeSpan.FromSeconds(90));

		// 🚀 ВАЖНО: ВЕСЬ КОД ОБОРАЧИВАЕМ В TRY СРАЗУ ПОСЛЕ ЗАХВАТА ЗАМКА!
		try
		{
			// Пытаемся получить разрешение на выполнение
			using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, context.CancellationToken);

			if (!lease.IsAcquired)
			{
				throw new RateLimitExceededException("Rate limit exceeded (Gemini). Triggering Redelivery.");
			}

			_logger.LogInformation($"[Reply] 🚀 Обработка диалога. BusinessID: {businessAccountId}, SenderID: {senderId}");

			// 2. Ищем настройки владельца бота в БД
			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);

			if (settings == null)
			{
				_logger.LogWarning($"[Reply] ❌ Настройки для BusinessID {businessAccountId} не найдены в БД. Игнорируем.");
				return;
			}

			_console.Init(settings.UserId, settings.Id);

			if (!settings.IsActive || !settings.IsDirectEnabled)
			{
				await _console.Log($"⏸ У бота выключены ответы на сообщения. Пропускаем.");
				return;
			}

			var accessInstaToken = settings.AccessToken;

			if (string.IsNullOrEmpty(accessInstaToken))
			{
				await _console.LogError($"❌ Токен отсутствует для BusinessID {businessAccountId}.");
				return;
			}

			var customer = await _db.InstagramBotCustomers
				.FirstOrDefaultAsync(c => c.InstagramSettingsId == settings.Id && c.InstagramSenderId == senderId);

			if (customer == null)
			{
				var customerInfo = await _instaService.GetInstagramUserProfileAsync(senderId, accessInstaToken);
				customer = new InstagramBotCustomer
				{
					InstagramSettingsId = settings.Id,
					InstagramSenderId = senderId,
					Username = customerInfo.Username,
					ProfilePictureUrl = customerInfo.ProfilePicUrl
				};
				_db.InstagramBotCustomers.Add(customer);
				await _db.SaveChangesAsync();
			}

			// А) Проверяем галочку "Игнорировать"
			if (customer.IsIgnored)
			{
				await _console.LogInfo($"Пользователь {senderId} в игнор-листе. Пропускаем диалог.");
				return;
			}

			// Б) Проверяем лимит сообщений в сутки
			var startOfPeriod = DateTimeNow.AddDays(-1);
			var messageCount = await _db.BotResponseLogs
				.CountAsync(log => log.CustomerId == customer.Id && log.RespondedAt >= startOfPeriod);

			if (messageCount > settings.MaxAnswerMessagesCount)
			{
				await _console.LogWarning($"Аккаунт {customer.Username} превысил лимит сообщений: {messageCount}/{settings.MaxAnswerMessagesCount}");
				return;
			}

			if (messageCount == settings.MaxAnswerMessagesCount)
			{
				// 🚀 БЕРЕМ ТЕКСТ ИЗ НАСТРОЕК БД (с поддержкой Spintax)
				string rawLimitMessage = !string.IsNullOrWhiteSpace(settings.DailyLimitMessage)
					? settings.DailyLimitMessage
					: "Sorry, but I need to go urgently, let's write tomorrow. Bye 🧡";

				// Применяем Spintax-рандомайзер (выберет случайный вариант, если есть скобки {})
				string finalFarewellMessage = InstagramCommentEngine.GetRandomTemplate(rawLimitMessage);

				await _instaService.SendMessageAsync(senderId, finalFarewellMessage, accessInstaToken);
				await _console.LogInfo($"Отправлено последнее сообщение на сегодня пользователю {customer.Username}: «{finalFarewellMessage}»");
				await SaveCustomerLog(context.Message.ReplyId, customer.Id);
				return;
			}

			// В) Проверяем лимит токенов
			var tokensUsed = await _db.BotResponseLogs
				.Where(log => log.CustomerId == customer.Id && log.RespondedAt >= startOfPeriod)
				.SumAsync(log => log.TokensSpent);

			if (tokensUsed >= settings.MaxAnswersTokensCount)
			{
				await _console.LogWarning($"Аккаунт {customer.Username} превысил лимит токенов!");
				return;
			}

			// Г) Получаем историю переписки
			var messages = await _instaService.GetHistoryAsync(senderId, accessInstaToken, 10);

			if (messages == null || messages.Count == 0) return;

			// 🚀 СВЕРХВАЖНАЯ ПРОВЕРКА: если последнее сообщение УЖЕ от нашего бота (пока мы ждали) — выходим!
			var lastMsg = messages[0];
			string lastSenderId = lastMsg.From.Id;
			if (lastSenderId == businessAccountId)
			{
				await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
				_logger.LogInformation("Последнее сообщение уже от бота. Второй ответ не требуется.");
				return;
			}

			int unreadCount = 0;
			foreach (var msg in messages) { if (msg.From.Id != businessAccountId) unreadCount++; else break; }

			// Синхронизация пачки сообщений в Инбокс
			int networkTypeInstagram = 1;
			var conversation = await _inboxService.GetOrCreateConversationAsync(
				settings.ProfileId,
				networkTypeInstagram,
				settings.Id,
				senderId,
				customer.Username,
				customer.Username,
				customer.ProfilePictureUrl);

			if (unreadCount > 0)
			{
				for (int i = unreadCount - 1; i >= 0; i--)
				{
					var m = messages[i];
					if (m.From.Id != businessAccountId)
					{
						string content = await ResolveMessageContentAsync(m);
						await _inboxService.SaveIncomingMessageAsync(conversation.Id, m.Id, content);
					}
				}
			}

			// Проверка Human Takeover
			if (conversation.IsHumanTakeover)
			{
				if (conversation.HumanTakeoverUntil.HasValue && conversation.HumanTakeoverUntil.Value > DateTime.UtcNow)
				{
					await _console.LogInfo($"👤 Диалог с @{customer.Username} перехвачен менеджером (до {conversation.HumanTakeoverUntil.Value:HH:mm} UTC). Бот молчит.");
					return;
				}
				else if (!conversation.HumanTakeoverUntil.HasValue)
				{
					await _console.LogInfo($"👤 Диалог с @{customer.Username} в ручном режиме. Бот молчит.");
					return;
				}
			}

			var chatHistory = new List<AiRequest>();
			var unreadUserMessageIds = new List<string>();

			for (int i = messages.Count - 1; i >= 0; i--)
			{
				var msg = messages[i];
				string content = await ResolveMessageContentAsync(msg);

				if (content is null)
				{
					await _console.LogInfo("Медиа еще обрабатываются, Snooze...");
					await context.SchedulePublish(TimeSpan.FromSeconds(60), context.Message);
					return;
				}

				string role = msg.From.Id == businessAccountId ? "model" : "user";

				chatHistory.Add(new AiRequest
				{
					Role = role,
					Text = string.IsNullOrEmpty(content) ? "[Empty message]" : content
				});

				bool isUnread = i < unreadCount;
				if (isUnread && role == "user")
				{
					unreadUserMessageIds.Add(msg.Id);
				}
			}

			// Реакции (лайки на сообщения)
			var random = new Random();
			if (settings.IsReactionsEnabled && unreadUserMessageIds.Count > 0 && random.Next(1, 101) > 50)
			{
				try
				{
					var reaction = _instaService.GetRandomReaction(settings.AllowedReactions);
					string targetMessageId = unreadUserMessageIds[random.Next(unreadUserMessageIds.Count)];
					var isSuccess = await _instaService.SendReactionAsync(senderId, targetMessageId, reaction, accessInstaToken);
					if (isSuccess)
					{
						await _console.Log($"Отправлена реакция {reaction} пользователю {customer.Username} на сообщение");
						await Task.Delay(1500);
					}
				}
				catch (Exception ex)
				{
					await _console.LogError($"[Send reaction error] {ex.ToString()}");
				}
			}

			await _instaService.SetTypingStatusAsync(senderId, accessInstaToken);

			// Запрос к ИИ
			var systemPrompt = settings.SystemPrompt ?? "Ты полезный помощник.";

			var contextRedisKey = $"ig:user_context:{senderId}";
			string? userContextInfo = await _redis.StringGetAsync(contextRedisKey);

			if (string.IsNullOrEmpty(userContextInfo))
			{
				var (contextText, newAppearance) = await _instaService.GetUserContextForAiAsync(
					senderId,
					accessInstaToken,
					processPhotos: settings.ProcessPhotos,
					existingAppearance: customer.AppearanceDescription);

				userContextInfo = contextText;

				if (!string.IsNullOrEmpty(userContextInfo))
				{
					await _redis.StringSetAsync(contextRedisKey, userContextInfo, TimeSpan.FromHours(24));

					// 🚀 СОХРАНЯЕМ ВСЁ ДОСЬЕ В БАЗУ ДАННЫХ ДЛЯ ВКЛАДКИ AI-CHATS!
					customer.AiContext = userContextInfo;
				}

				if (!string.IsNullOrEmpty(newAppearance) && customer.AppearanceDescription != newAppearance)
				{
					customer.AppearanceDescription = newAppearance;
				}

				await _db.SaveChangesAsync();
				_logger.LogInformation("💾 [Profile Context] Досье клиента @{User} сохранено в БД!", customer.Username);
			}

			systemPrompt += "\n\nKeep this information in mind when responding. For example, whether you are mutual subscribers. If not, ask him to subscribe.\n"
				+ userContextInfo;

			var aiResponse = await _aiService.GetAnswerAsync(systemPrompt, chatHistory, null);

			if (string.IsNullOrWhiteSpace(aiResponse))
			{
				await _console.LogError("ИИ вернул пустой ответ.");

				string retryKey = $"retry_ai_{messageId}";
				long retryCount = await _redis.StringIncrementAsync(retryKey);

				if (retryCount == 1)
				{
					await _redis.KeyExpireAsync(retryKey, TimeSpan.FromMinutes(10));
				}

				if (retryCount <= 3)
				{
					await _console.Log($"Попытка {retryCount}/3. Отправим запрос в очередь через {60 * retryCount}сек...");
					await context.SchedulePublish(TimeSpan.FromSeconds(60 * retryCount), context.Message);
					return;
				}

				return;
			}

			// Метка эхо для нашего бота:
			var botReplyingKey = $"bot:replying:{businessAccountId}:{senderId}";
			await _redis.StringSetAsync(botReplyingKey, "1", TimeSpan.FromSeconds(60));

			// Отправка ответа в Instagram
			await SendLongMessageAsHumanAsync(senderId, aiResponse, accessInstaToken);

			// Сохранение в Инбокс
			await _inboxService.SaveBotReplyAsync(conversation.Id, aiResponse);

			await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
			await SaveCustomerLog(context.Message.ReplyId, customer.Id);

			await _console.Log($"Ответ успешно отправлен пользователю {customer.Username}");
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Internal && ex.Message.Contains("blocked"))
		{
			await _console.LogWarning("Gemini заблокировал контент. Прекращаем обработку.");
		}
		catch (Exception ex)
		{
			await _console.LogError($"Техническая ошибка. RabbitMQ сделает Retry. {ex.Message}");
			throw;
		}
		finally
		{
			// 🚀 ГАРАНТИРОВАННОЕ СНЯТИЕ ЗАМКА ДИАЛОГА: срабатывает при ЛЮБОМ исходе!
			await _redis.KeyDeleteAsync(dialogBusyKey);
		}
	}

	private async Task SaveCustomerLog(string replyId, int id)
	{
		try
		{
			var responseLog = new BotResponseLog
			{
				CustomerId = id,
				MessageId = replyId, // ID последнего сообщения Инсты
				TokensSpent = 0, // Пока 0
				RespondedAt = DateTimeNow
			};
			_db.BotResponseLogs.Add(responseLog);
			await _db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			await _console.LogError($"Ошибка сохранения инфы ответе пользователю {ex.ToString()}");
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
				//await _instaService.SetTypingStatusAsync(userId, token);

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