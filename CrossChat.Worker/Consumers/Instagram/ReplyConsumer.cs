using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Helpers;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Interfaces.Google;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Site;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Exceptions;
using Grpc.Core;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Telegram.Bot.Types.ReplyMarkups;
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
	private readonly IGeminiDecisionService _decisionService;
	private readonly ITelegramService _telegramService;
	private readonly IVoiceService _voiceService;
	private readonly SiteSettings _siteSettings;
	private readonly IDatabase _redis;
	private readonly IGoogleDriveUploader _googleDriveUploader;

	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 20,
		Window = TimeSpan.FromMinutes(1),
		QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
		QueueLimit = 0
	});

	public ReplyConsumer(
		ILogger<ReplyConsumer> logger,
		AppDbContext db,
		IInstagramService instaService,
		IAiService aiService,
		IConnectionMultiplexer redisMux,
		IInstagramConsole console,
		IInboxService inboxService,
		IGeminiDecisionService decisionService,
		ITelegramService telegramService,
		IVoiceService voiceService,
		SiteSettings siteSettings,
		IGoogleDriveUploader googleDriveUploader)
	{
		_logger = logger;
		_db = db;
		_instaService = instaService;
		_aiService = aiService;
		_console = console;
		_inboxService = inboxService;
		_decisionService = decisionService;
		_redis = redisMux.GetDatabase();
		_telegramService = telegramService;
		_voiceService = voiceService;
		_siteSettings = siteSettings;
		_googleDriveUploader = googleDriveUploader;
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
		var messageId = context.Message.ReplyId;
		var processingKey = $"processed:{messageId}";

		if (await _redis.KeyExistsAsync(processingKey))
		{
			_logger.LogInformation("Сообщение уже обработано успешно, игнорируем дубль.");
			return;
		}

		var senderId = context.Message.SenderId;
		var businessAccountId = context.Message.RecipientId;

		var targetTimeKey = $"debounce:target_time:ig:{senderId}:{businessAccountId}";
		var activeTimerKey = $"debounce:timer_active:ig:{senderId}:{businessAccountId}";

		var storedTicks = await _redis.StringGetAsync(targetTimeKey);
		if (storedTicks.HasValue && long.TryParse(storedTicks, out long ticks))
		{
			var targetTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
			var remaining = targetTimeUtc - DateTime.UtcNow;

			if (remaining > TimeSpan.FromSeconds(2))
			{
				_logger.LogInformation($"[Instagram Debounce] ⏳ Пользователь {senderId} еще пишет! Откладываем ответ еще на {remaining.TotalSeconds:F0} сек...");
				await context.SchedulePublish(remaining, context.Message);
				return;
			}
		}

		await _redis.KeyDeleteAsync(targetTimeKey);
		await _redis.KeyDeleteAsync(activeTimerKey);

		var dialogBusyKey = $"lock:dialog_busy:{businessAccountId}:{senderId}";

		if (await _redis.KeyExistsAsync(dialogBusyKey))
		{
			_logger.LogInformation("[Reply] ⏳ Диалог {SenderId} уже генерирует ответ в другом потоке. Откладываем...", senderId);
			await context.SchedulePublish(TimeSpan.FromSeconds(10), context.Message);
			return;
		}

		await _redis.StringSetAsync(dialogBusyKey, "busy", TimeSpan.FromSeconds(90));

		try
		{
			using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, context.CancellationToken);
			if (!lease.IsAcquired)
			{
				throw new RateLimitExceededException("Rate limit exceeded (Gemini). Triggering Redelivery.");
			}

			_logger.LogInformation($"[Reply] 🚀 Обработка диалога. BusinessID: {businessAccountId}, SenderID: {senderId}");

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.Include(s => s.User)
				.Include(s => s.Profile)
					.ThenInclude(p => p.VoiceSetting)
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

			if (customer.IsIgnored)
			{
				await _console.LogInfo($"Пользователь {senderId} в игнор-листе. Пропускаем диалог.");
				return;
			}

			var startOfPeriod = DateTimeNow.AddDays(-1);
			var messageCount = await _db.BotResponseLogs
				.CountAsync(log => log.CustomerId == customer.Id && log.RespondedAt >= startOfPeriod);

			if (messageCount > settings.MaxAnswerMessagesCount)
			{
				await _console.LogWarning($"Аккаунт {customer.Username} превысил лимит сообщений: {messageCount}/{settings.MaxAnswerMessagesCount} (бот на паузе до завтра)");
				return;
			}

			bool isLastMessageForToday = (messageCount == settings.MaxAnswerMessagesCount);

			var tokensUsed = await _db.BotResponseLogs
				.Where(log => log.CustomerId == customer.Id && log.RespondedAt >= startOfPeriod)
				.SumAsync(log => log.TokensSpent);

			if (tokensUsed >= settings.MaxAnswersTokensCount)
			{
				await _console.LogWarning($"Аккаунт {customer.Username} превысил лимит токенов!");
				return;
			}

			var messages = await _instaService.GetHistoryAsync(senderId, accessInstaToken, 10);
			if (messages == null || messages.Count == 0) return;

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
						await _inboxService.SaveIncomingMessageAsync(conversation.Id, m.Id, content ?? "[Вложение]");
					}
				}
			}

			// 🧪 ФИЛЬТР ТЕСТ-РЕЖИМА (БЕЛЫЙ СПИСОК):
			if (settings.BotFilterMode == 1 &&
				!InstagramWhitelistHelper.IsInWhitelist(settings.BotWhitelist, customer.Username, senderId))
			{
				await _console.LogInfo($"🧪 [Test Mode] Сообщение от @{customer.Username ?? senderId} сохранено в Инбокс, но ИИ и Decision 1 отключены (не в белом списке).");
				await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
				return;
			}

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

			var recentSavedRows = await _db.InboxMessages
				.AsNoTracking()
				.Where(m => m.ConversationId == conversation.Id && m.ExternalMessageId != null)
				.OrderByDescending(m => m.CreatedAtUtc)
				.Take(20)
				.Select(m => new { m.ExternalMessageId, m.Text })
				.ToListAsync();

			var savedTextByExternalId = new Dictionary<string, string>();
			foreach (var row in recentSavedRows)
			{
				if (!string.IsNullOrEmpty(row.ExternalMessageId) && !string.IsNullOrEmpty(row.Text))
					savedTextByExternalId.TryAdd(row.ExternalMessageId, row.Text);
			}

			var chatHistory = new List<AiRequest>();
			var unreadUserMessageIds = new List<string>();
			var turnMediaUsages = new List<MediaDataEntry>();

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

				if (content == "[Empty message]" &&
					!string.IsNullOrEmpty(msg.Id) &&
					savedTextByExternalId.TryGetValue(msg.Id, out var savedInboxText))
				{
					content = savedInboxText;
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

					if (!string.IsNullOrEmpty(msg.Id) && MediaMessageStorage.Storage.TryGetValue(msg.Id, out var mediaEntries))
					{
						lock (mediaEntries)
						{
							turnMediaUsages.AddRange(mediaEntries.Where(x => x.TotalTokens > 0));
						}
					}
				}
			}

			// =========================================================================
			// 🧠 ФИЛЬТР GEMINI DECISION 1
			// =========================================================================
			var transcriptBuilder = new System.Text.StringBuilder();
			foreach (var item in chatHistory)
			{
				string speaker = item.Role == "model" ? "[Assistant]" : "[User]";
				transcriptBuilder.AppendLine($"{speaker}: {item.Text}");
			}

			List<MediaCategoryPromptOption>? mediaCategoryOptions = null;
			List<InstagramMediaItem> availableGalleryItems = new();
			HashSet<int> sentMediaIds = new();
			int totalGalleryCount = 0;

			if (settings.IsAutoMediaEnabled)
			{
				var botCategories = await _db.InstagramMediaCategories
					.AsNoTracking()
					.Where(c => c.InstagramSettingsId == settings.Id)
					.ToListAsync();

				var allItems = await _db.InstagramMediaItems
					.AsNoTracking()
					.Where(i => i.InstagramSettingsId == settings.Id)
					.ToListAsync();

				totalGalleryCount = allItems.Count;

				if (totalGalleryCount > 0)
				{
					sentMediaIds = (await _db.InstagramCustomerSentMedias
						.AsNoTracking()
						.Where(s => s.CustomerId == customer.Id)
						.Select(s => s.MediaItemId)
						.ToListAsync())
						.ToHashSet();

					availableGalleryItems = allItems
						.Where(i => !sentMediaIds.Contains(i.Id))
						.ToList();

					if (availableGalleryItems.Count > 0)
					{
						mediaCategoryOptions = botCategories
							.Where(c => allItems.Any(i => i.CategoryId == c.Id))
							.Select(c => new MediaCategoryPromptOption
							{
								Id = c.Id,
								Name = c.Name,
								Description = c.Description,
								IsDefault = c.IsDefault
							})
							.ToList();
					}
				}
			}

			var decision = await _decisionService.EvaluateDirectReplyAsync(
				transcriptBuilder.ToString(),
				context.CancellationToken,
				mediaCategoryOptions);

			bool shouldExtractPersonalFact = false;
			bool shouldRespondWithVoice = false;
			var voiceCooldownKey = $"voice:cooldown:{businessAccountId}:{senderId}";
			var mediaCooldownKey = $"media:cooldown:{businessAccountId}:{senderId}";
			InstagramMediaItem? selectedMediaToSend = null;
			string? selectedMediaCategoryName = null;

			if (decision.IsSuccess)
			{
				string logMsg =
					$"🧠 [Decision 1] @{customer.Username} | Ответ: {decision.NeedsReplyProb:P0} | " +
					$"Спам: {decision.IsSpamProb:P0} | Ссылка: {decision.WantsLinkProb:P0} | " +
					$"Человек: {decision.NeedsHumanProb:P0} | Голос: {decision.ShouldVoiceReplyProb:P0} | " +
					$"Факт: {decision.ContainsPersonalFactProb:P0} | Медиа: {decision.WantsMediaProb:P0} | " +
					$"{decision.ElapsedMs} мс";

				_logger.LogInformation(logMsg);
				await _console.LogInfo(logMsg);

				// 1. СПАМ / РЕКЛАМА (>= 85%) -> ПОЛНЫЙ ИГНОР
				if (decision.IsSpamProb >= 0.85)
				{
					await _console.LogWarning($"🚫 [Decision 1] Сообщение от @{customer.Username} признано спамом ({decision.IsSpamProb:P0}). Игнорируем.");
					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
					return;
				}

				// 2. ПРОСИТ ССЫЛКУ / TELEGRAM ИЛИ НУЖЕН ЖИВОЙ ЧЕЛОВЕК -> ПЕРЕДАЁМ ВЛАДЕЛЬЦУ, БОТ МОЛЧИТ!
				bool wantsLink = decision.WantsLinkProb >= 0.70;
				bool needsHuman = decision.NeedsHumanProb >= 0.75;

				if (wantsLink || needsHuman)
				{
					await _inboxService.ToggleHumanTakeoverAsync(conversation.Id, true);

					string reasonTitle = wantsLink
						? "🔗 <b>КЛИЕНТ ПРОСИТ ССЫЛКУ / TELEGRAM</b>"
						: "🙋 <b>ДИАЛОГ ТРЕБУЕТ УЧАСТИЯ ВЛАДЕЛЬЦА</b>";

					string lastUserMsgText = chatHistory.LastOrDefault(m => m.Role == "user")?.Text ?? "[Вложение]";
					string safeClientName = WebUtility.HtmlEncode(customer.Username ?? senderId);
					string safeBotName = WebUtility.HtmlEncode(settings.Username ?? "Instagram");
					string safeProfileName = WebUtility.HtmlEncode(settings.Profile?.Name ?? "Профиль");
					string safeText = WebUtility.HtmlEncode(lastUserMsgText);

					long? ownerTgId = settings.User?.TelegramUserId;
					if (ownerTgId.HasValue)
					{
						string tgMessage =
							$"{reasonTitle}\n\n" +
							$"<b>Профиль:</b> {safeProfileName} (@{safeBotName})\n" +
							$"<b>Клиент:</b> @{safeClientName}\n" +
							$"<b>Сообщение:</b> «{safeText}»\n\n" +
							$"⏸ <i>ИИ поставлен на паузу на 2 часа, чтобы вы могли ответить лично.</i>";

						var inlineKeyboard = new InlineKeyboardMarkup(new[]
						{
							InlineKeyboardButton.WithCallbackData("🤖 Вернуть диалог боту", $"resume_ai:{conversation.Id}")
						});

						try
						{
							await _telegramService.SendMessage(ownerTgId.Value, tgMessage, inlineKeyboard);
							await _console.Log($"📨 [Human Takeover] Отправлено уведомление владельцу в Telegram (conv #{conversation.Id}). Бот молчит.");
						}
						catch (Exception tgEx)
						{
							await _console.LogWarning($"⚠️ Не удалось отправить уведомление владельцу {ownerTgId.Value} в Telegram: {tgEx.Message}");
						}
					}
					else
					{
						await _console.LogWarning($"⚠️ [Human Takeover] Включена пауза на 2ч для @{customer.Username}, но владелец не привязал Telegram к аккаунту.");
					}

					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
					return;
				}

				// 3. ПРОВЕРЯЕМ ГОТОВНОСТЬ ГОЛОСОВОГО ОТВЕТА
				bool userSentVoiceJustNow = chatHistory
					.Where(m => m.Role == "user")
					.TakeLast(Math.Max(1, unreadCount))
					.Any(m => m.Text.Contains("[voice message]", StringComparison.OrdinalIgnoreCase));

				if (decision.ShouldVoiceReplyProb >= 0.75)
				{
					var voiceSetting = settings.Profile?.VoiceSetting;
					if (voiceSetting == null || !voiceSetting.IsActive)
					{
						await _console.LogInfo($"🎙️ [Decision 1] Модель предложила голос ({decision.ShouldVoiceReplyProb:P0}), но настройки голоса профиля отключены — отвечаем текстом.");
					}
					else if (!userSentVoiceJustNow && await _redis.KeyExistsAsync(voiceCooldownKey))
					{
						await _console.LogInfo($"⏳ [Voice Cooldown] Недавно уже отправляли ГС @{customer.Username} — отвечаем текстом, чтобы не спамить аудио.");
					}
					else
					{
						shouldRespondWithVoice = true;
						await _console.Log($"🎙️ [Decision 1] Ответ будет сгенерирован голосом профиля ({decision.ShouldVoiceReplyProb:P0}).");
					}
				}

				// 4. ВЫБОР ФОТО/ВИДЕО ИЗ МЕДИАТЕКИ ПО КАТЕГОРИЯМ
				if (availableGalleryItems.Count > 0 && decision.WantsMediaProb >= 0.72)
				{
					bool isMediaOnCooldown = await _redis.KeyExistsAsync(mediaCooldownKey);
					if (isMediaOnCooldown && decision.WantsMediaProb < 0.90)
					{
						await _console.LogInfo($"⏳ [Media Cooldown] Недавно уже отправляли медиа @{customer.Username} ({decision.WantsMediaProb:P0}) — пропускаем.");
					}
					else
					{
						int? targetCategoryId = null;

						var bestCat = decision.MediaCategoryProbs
							.Where(kv => kv.Value >= 0.55)
							.OrderByDescending(kv => kv.Value)
							.FirstOrDefault();

						if (bestCat.Key > 0)
						{
							var itemsInBest = availableGalleryItems.Where(i => i.CategoryId == bestCat.Key).ToList();
							if (itemsInBest.Count > 0)
							{
								targetCategoryId = bestCat.Key;
								selectedMediaToSend = itemsInBest[Random.Shared.Next(itemsInBest.Count)];
							}
						}

						if (selectedMediaToSend == null)
						{
							var defaultCat = mediaCategoryOptions?.FirstOrDefault(c => c.IsDefault);
							if (defaultCat != null)
							{
								var itemsInDefault = availableGalleryItems.Where(i => i.CategoryId == defaultCat.Id).ToList();
								if (itemsInDefault.Count > 0)
								{
									targetCategoryId = defaultCat.Id;
									selectedMediaToSend = itemsInDefault[Random.Shared.Next(itemsInDefault.Count)];
								}
							}
						}

						if (selectedMediaToSend != null)
						{
							selectedMediaCategoryName = mediaCategoryOptions?.FirstOrDefault(c => c.Id == targetCategoryId)?.Name ?? "Общее";
							shouldRespondWithVoice = false;
							await _console.Log($"🖼️ [Decision 1] Выбрано {selectedMediaToSend.MediaType} из «{selectedMediaCategoryName}» ({decision.WantsMediaProb:P0}).");
						}
						else
						{
							await _console.LogInfo("🖼️ [Media Gallery] В нужной и дефолтной категориях нет неотправленных файлов для этого клиента — пропускаем.");
						}
					}
				}

				shouldExtractPersonalFact = decision.ContainsPersonalFactProb >= 0.60;

				// 5. СМАЙЛИК, СТИКЕР ИЛИ "ОК, СПАСИБО" -> СТАВИМ РЕАКЦИЮ И ВЫХОДИМ,
				// ТОЛЬКО ЕСЛИ НЕ НУЖНО ОТПРАВЛЯТЬ НИ МЕДИА, НИ ГОЛОСОВОЕ!
				if (decision.NeedsReplyProb < 0.45 && selectedMediaToSend == null && !shouldRespondWithVoice)
				{
					await _console.Log($"💬 [Decision 1] Текстовый ответ для @{customer.Username} не требуется ({decision.NeedsReplyProb:P0}). Ставим реакцию.");

					if (settings.IsReactionsEnabled && unreadUserMessageIds.Count > 0)
					{
						try
						{
							var reaction = _instaService.GetRandomReaction(settings.AllowedReactions);
							string targetMessageId = unreadUserMessageIds.Last();
							bool isSuccess = await _instaService.SendReactionAsync(senderId, targetMessageId, reaction, accessInstaToken);
							if (isSuccess)
							{
								await _console.Log($"❤️ [Decision 1] Поставили реакцию {reaction} на сообщение @{customer.Username}.");
							}
						}
						catch (Exception ex)
						{
							await _console.LogError($"[Decision 1 Reaction Error] {ex.Message}");
						}
					}

					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);
					return;
				}
			}
			else
			{
				await _console.LogWarning($"⚠️ [Decision 1] Не сработал ({decision.ErrorMessage}). Идём стандартно в ИИ.");
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

				if (!string.IsNullOrEmpty(contextText))
				{
					string existingMemory = ExtractSection(customer.AiContext, MemorySectionMarker, MediaStatsSectionMarker);
					string existingMediaStats = ExtractSection(customer.AiContext, MediaStatsSectionMarker, null);
					userContextInfo = CombineAiContext(contextText, existingMemory, existingMediaStats);

					await _redis.StringSetAsync(contextRedisKey, userContextInfo, TimeSpan.FromHours(24));
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

			if (isLastMessageForToday)
			{
				var farewellInstruction = !string.IsNullOrWhiteSpace(settings.DailyLimitPrompt)
					? settings.DailyLimitPrompt
					: "Это твой последний ответ на сегодня, так как дневной лимит сообщений исчерпан. Обязательно ответь на вопрос собеседника, но в конце мягко и естественно попрощайся до завтра в своем стиле (например: убегаешь по делам / на съемку / спать). Будь краткой и теплой. Отвечай строго на языке собеседника!";

				systemPrompt += $"\n\n{farewellInstruction}";
				_logger.LogInformation("👋 [Reply] Генерируем персонализированное прощание ИИ для @{User}", customer.Username);
			}

			if (shouldRespondWithVoice)
			{
				systemPrompt +=
					"\n\nNote for voice reply: Your answer will be synthesized into a natural voice message. " +
					"Reply in 1-3 warm, conversational, human-like sentences. " +
					"STRICT RULE: NEVER include any URLs, website links, domain names, t.me links, @usernames, or emojis. " +
					"Do NOT use markdown (*, **, #) or bullet lists. " +
					"You may naturally add subtle vocal cues where appropriate: <chuckle>, <sigh>, <whisper>, <short pause>.";
			}

			if (selectedMediaToSend != null)
			{
				string mediaLabel = selectedMediaToSend.MediaType == "video" ? "short video" : "photo";
				systemPrompt +=
					$"\n\nNote for this reply (do NOT print any tags or brackets like [MEDIA] in your message): " +
					$"Right after this text message, your {mediaLabel} (category: '{selectedMediaCategoryName}') will be sent automatically. " +
					"Write 1-2 natural sentences accompanying it (and do NOT say you cannot send photos or videos).";
			}
			else if (settings.IsAutoMediaEnabled && totalGalleryCount > 0 && availableGalleryItems.Count == 0)
			{
				systemPrompt +=
					"\n\nNote for this reply (do NOT print any tags or brackets): " +
					"All available gallery photos/videos have already been sent to this user. " +
					"Do NOT promise to send a new photo or video right now.";
			}

			var aiResult = await _aiService.GetAnswerWithUsageAsync(systemPrompt, chatHistory, null);
			var aiResponse = aiResult?.Text ?? string.Empty;

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

			// Случайная реакция — только после успешного ответа ИИ (чтобы не дублировалась при retry RabbitMQ)
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

			var botReplyingKey = $"bot:replying:{businessAccountId}:{senderId}";
			await _redis.StringSetAsync(botReplyingKey, "1", TimeSpan.FromSeconds(60));

			if (shouldRespondWithVoice && ContainsUrl(aiResponse))
			{
				shouldRespondWithVoice = false;
				await _console.LogInfo("🔗 [Voice Guard] В ответе ИИ есть ссылка — отменяем озвучку и отправляем текстом, чтобы ссылка была кликабельной.");
			}

			bool sentAsVoice = false;

			if (shouldRespondWithVoice)
			{
				try
				{
					var voiceSetting = settings.Profile!.VoiceSetting!;
					var voiceRequest = new VoiceGenerationRequest
					{
						Text = aiResponse.Trim(),
						ProfileName = settings.Profile.Name ?? "Creator",
						VoiceName = voiceSetting.VoiceName,
						Style = voiceSetting.Style,
						Pace = voiceSetting.Pace,
						Accent = voiceSetting.Accent,
						Scene = voiceSetting.Scene,
						SampleContext = voiceSetting.SampleContext,
						Temperature = voiceSetting.Temperature
					};

					byte[] rawVoiceBytes = await _voiceService.GenerateVoiceAudioAsync(voiceRequest, voiceSetting.Provider);

					if (rawVoiceBytes != null && rawVoiceBytes.Length > 0)
					{
						string m4aPath = await VideoService.SaveTtsAudioAsInstagramM4aAsync(
							rawVoiceBytes,
							_siteSettings.TempFolder,
							_logger);

						string m4aFileName = Path.GetFileName(m4aPath);
						string publicAudioUrl = $"{_siteSettings.AppUrl.TrimEnd('/')}/temp_media/{m4aFileName}";

						await _instaService.SendMediaAttachmentAsync(senderId, "audio", publicAudioUrl, accessInstaToken);

						string readableTranscript = Regex.Replace(aiResponse, @"<[^>]+>", "").Trim();
						await _inboxService.SaveBotReplyAsync(
							conversation.Id,
							$"🎙️ {readableTranscript}",
							mediaUrl: publicAudioUrl,
							mediaType: "audio");

						sentAsVoice = true;
						await _redis.StringSetAsync(voiceCooldownKey, "1", TimeSpan.FromMinutes(25));
						await _console.Log($"🎙️ Голосовой ответ успешно отправлен пользователю {customer.Username}");
					}
				}
				catch (Exception voiceEx)
				{
					_logger.LogWarning(voiceEx, "⚠️ [Voice Reply] Ошибка при генерации/отправке голосового ответа для @{User}", customer.Username);
					await _console.LogWarning($"⚠️ Не удалось отправить голосовой ответ ({voiceEx.Message}). Отправляем обычным текстом.");
				}
			}

			if (!sentAsVoice)
			{
				string cleanTextReply = Regex.Replace(
					aiResponse,
					@"<[^>]+>|\[(?:MEDIA(?:\s+ATTACHED)?|PHOTO|VIDEO|VOICE(?:\s+REPLY\s+MODE)?)\]\s*:?",
					"",
					RegexOptions.IgnoreCase).Trim();
				await SendLongMessageAsHumanAsync(senderId, cleanTextReply, accessInstaToken);
				await _inboxService.SaveBotReplyAsync(conversation.Id, cleanTextReply);
				await _console.Log($"Ответ успешно отправлен пользователю {customer.Username}");
			}

			if (selectedMediaToSend != null)
			{
				try
				{
					if (!Directory.Exists(_siteSettings.TempFolder))
						Directory.CreateDirectory(_siteSettings.TempFolder);

					string ext = Path.GetExtension(selectedMediaToSend.FileName);
					if (string.IsNullOrWhiteSpace(ext))
						ext = selectedMediaToSend.MediaType == "video" ? ".mp4" : ".jpg";

					string tempFileName = $"gal_{Guid.NewGuid():N}{ext}";
					string tempLocalPath = Path.Combine(_siteSettings.TempFolder, tempFileName);

					await _googleDriveUploader.DownloadFileAsync(selectedMediaToSend.GoogleDriveFileId, tempLocalPath);
					string publicMediaUrl = $"{_siteSettings.AppUrl.TrimEnd('/')}/temp_media/{tempFileName}";

					await Task.Delay(1200);
					await _redis.StringSetAsync(botReplyingKey, "1", TimeSpan.FromSeconds(60));

					await _instaService.SendMediaAttachmentAsync(
						senderId,
						selectedMediaToSend.MediaType,
						publicMediaUrl,
						accessInstaToken);

					_db.InstagramCustomerSentMedias.Add(new InstagramCustomerSentMedia
					{
						CustomerId = customer.Id,
						MediaItemId = selectedMediaToSend.Id,
						SentAtUtc = DateTime.UtcNow
					});

					int newSentCount = sentMediaIds.Count + 1;
					string mediaStatsText = $"Отправлено медиа из галереи: {newSentCount} из {totalGalleryCount} (последнее: «{selectedMediaCategoryName}», {selectedMediaToSend.MediaType})";

					string rawCtx = !string.IsNullOrWhiteSpace(customer.AiContext) ? customer.AiContext : (userContextInfo ?? "");
					string profilePart = ExtractProfileSection(rawCtx);
					string memoryPart = ExtractSection(rawCtx, MemorySectionMarker, MediaStatsSectionMarker);

					customer.AiContext = CombineAiContext(profilePart, memoryPart, mediaStatsText);
					await _db.SaveChangesAsync();

					await _redis.StringSetAsync(contextRedisKey, customer.AiContext, TimeSpan.FromHours(24));
					await _redis.StringSetAsync(mediaCooldownKey, "1", TimeSpan.FromMinutes(20));

					string inboxLabel = selectedMediaToSend.MediaType == "video" ? "🎬 Видеозапись" : "📷 Фотография";
					await _inboxService.SaveBotReplyAsync(
						conversation.Id,
						inboxLabel,
						mediaUrl: publicMediaUrl,
						mediaType: selectedMediaToSend.MediaType);

					await _console.Log($"🖼️ Отправлено {selectedMediaToSend.MediaType} из «{selectedMediaCategoryName}» клиенту {customer.Username} ({newSentCount}/{totalGalleryCount}).");
				}
				catch (Exception mediaEx)
				{
					_logger.LogWarning(mediaEx, "⚠️ [Media Gallery] Ошибка отправки медиа #{Id} для @{User}", selectedMediaToSend.Id, customer.Username);
					await _console.LogWarning($"⚠️ Не удалось отправить медиа из галереи: {mediaEx.Message}");
				}
			}

			await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(6), When.NotExists);

			// 🧠 Обновляем долгую память ТОЛЬКО если Decision 1 увидел новый личный факт
			AiGenerationResult? memoryResult = null;
			if (shouldExtractPersonalFact)
			{
				memoryResult = await TryUpdateLongTermMemoryAsync(
					customer,
					chatHistory,
					unreadCount,
					userContextInfo,
					contextRedisKey);
			}

			// 🪙 Считаем все токены за этот ход (Decision 1 + Медиа + Чат + Память) и пишем в БД
			await SaveCustomerLog(
				context.Message.ReplyId,
				customer.Id,
				decision,
				turnMediaUsages,
				aiResult,
				memoryResult);
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
			await _redis.KeyDeleteAsync(dialogBusyKey);
		}
	}

	private static bool ContainsUrl(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return false;
		return Regex.IsMatch(
			text,
			@"(?:https?://|www\.|t\.me/|\b[a-z0-9-]+\.(?:com|ru|net|org|io|me|app|club|pro|by|ua|kz)\b(?:/\S*)?)",
			RegexOptions.IgnoreCase);
	}

	private async Task SaveCustomerLog(
		string replyId,
		int customerId,
		DirectReplyDecisionResult decision,
		List<MediaDataEntry> mediaUsages,
		AiGenerationResult? chatResult,
		AiGenerationResult? memoryResult)
	{
		try
		{
			var usageSteps = new List<object>();
			int turnInputTokens = 0;
			int turnOutputTokens = 0;

			if (decision.IsSuccess && decision.InputTokens > 0)
			{
				turnInputTokens += decision.InputTokens;
				usageSteps.Add(new { step = "decision", model = "gemini-decision-1", input = decision.InputTokens, output = 0 });
			}

			foreach (var me in mediaUsages)
			{
				int mOut = me.OutputTokens > 0 ? me.OutputTokens : Math.Max(0, me.TotalTokens - me.InputTokens);
				turnInputTokens += me.InputTokens;
				turnOutputTokens += mOut;
				usageSteps.Add(new { step = $"media_{me.MediaType}", model = me.ModelName ?? "", input = me.InputTokens, output = mOut });
			}

			if (chatResult != null && chatResult.TotalTokens > 0)
			{
				int chatOut = chatResult.OutputTokens + chatResult.ThoughtTokens;
				turnInputTokens += chatResult.InputTokens;
				turnOutputTokens += chatOut;
				usageSteps.Add(new { step = "chat", model = chatResult.ModelName, input = chatResult.InputTokens, output = chatOut });
			}

			if (memoryResult != null && memoryResult.TotalTokens > 0)
			{
				int memOut = memoryResult.OutputTokens + memoryResult.ThoughtTokens;
				turnInputTokens += memoryResult.InputTokens;
				turnOutputTokens += memOut;
				usageSteps.Add(new { step = "memory", model = memoryResult.ModelName, input = memoryResult.InputTokens, output = memOut });
			}

			var responseLog = new BotResponseLog
			{
				CustomerId = customerId,
				MessageId = replyId,
				TokensSpent = turnInputTokens + turnOutputTokens,
				InputTokens = turnInputTokens,
				OutputTokens = turnOutputTokens,
				ModelName = chatResult?.ModelName,
				UsageDetailsJson = JsonSerializer.Serialize(usageSteps),
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

	private const string MemorySectionMarker = "MEMORY FACTS (LEARNED FROM CONVERSATION):";
	private const string MediaStatsSectionMarker = "GALLERY MEDIA STATS:";

	private static string ExtractProfileSection(string? aiContext)
	{
		if (string.IsNullOrWhiteSpace(aiContext)) return string.Empty;

		int idxMem = aiContext.IndexOf(MemorySectionMarker, StringComparison.OrdinalIgnoreCase);
		int idxMed = aiContext.IndexOf(MediaStatsSectionMarker, StringComparison.OrdinalIgnoreCase);

		int cutIdx = new[] { idxMem, idxMed }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
		return cutIdx >= 0 ? aiContext.Substring(0, cutIdx).Trim() : aiContext.Trim();
	}

	private static string ExtractSection(string? aiContext, string marker, string? nextMarker)
	{
		if (string.IsNullOrWhiteSpace(aiContext)) return string.Empty;
		int start = aiContext.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (start < 0) return string.Empty;

		int bodyStart = start + marker.Length;
		int end = !string.IsNullOrEmpty(nextMarker)
			? aiContext.IndexOf(nextMarker, bodyStart, StringComparison.OrdinalIgnoreCase)
			: -1;

		return (end >= 0 ? aiContext.Substring(bodyStart, end - bodyStart) : aiContext.Substring(bodyStart)).Trim();
	}

	private static string CombineAiContext(string? profilePart, string? memoryBullets, string? mediaStats = null)
	{
		var parts = new List<string>();
		if (!string.IsNullOrWhiteSpace(profilePart)) parts.Add(profilePart.Trim());
		if (!string.IsNullOrWhiteSpace(memoryBullets)) parts.Add($"{MemorySectionMarker}\n{memoryBullets.Trim()}");
		if (!string.IsNullOrWhiteSpace(mediaStats)) parts.Add($"{MediaStatsSectionMarker}\n{mediaStats.Trim()}");
		return string.Join("\n\n", parts);
	}

	private async Task<AiGenerationResult?> TryUpdateLongTermMemoryAsync(
		InstagramBotCustomer customer,
		List<AiRequest> chatHistory,
		int unreadCount,
		string? fallbackProfileContext,
		string contextRedisKey)
	{
		try
		{
			var latestUserMessages = chatHistory
				.Where(m => m.Role == "user")
				.TakeLast(Math.Max(1, Math.Min(unreadCount, 3)))
				.Select(m => m.Text)
				.ToList();

			if (latestUserMessages.Count == 0) return null;

			string existingMemory = ExtractSection(customer.AiContext, MemorySectionMarker, MediaStatsSectionMarker);
			string existingBlock = string.IsNullOrWhiteSpace(existingMemory) ? "None" : existingMemory;
			string existingMediaStats = ExtractSection(customer.AiContext, MediaStatsSectionMarker, null);
			string latestBlock = string.Join("\n", latestUserMessages);

			string prompt =
				"You maintain a concise long-term memory dossier about an Instagram user for an AI assistant.\n\n" +
				$"Existing memory facts:\n{existingBlock}\n\n" +
				$"Latest user message(s):\n{latestBlock}\n\n" +
				"Task:\n" +
				"Extract any NEW lasting personal facts about the user from the latest message(s) " +
				"(real name, city/country, job/occupation, hobbies, pets, relationships, birthday/plans, stable preferences) " +
				"and merge them with existing facts.\n" +
				"Rules:\n" +
				"- Replace outdated/conflicting facts (e.g. if user moved to a new city, update it).\n" +
				"- Keep max 15 ultra-concise bullet points in Russian, each starting with \"- \".\n" +
				"- Do NOT invent anything. Ignore small talk, compliments, or temporary phrases like 'how are you'.\n" +
				"- If there are NO new lasting facts to add or update, output EXACTLY: NO_CHANGES\n" +
				"- Otherwise output ONLY the updated bullet list starting with \"- \".";

			var extracted = await _aiService.GeminiRequestWithUsageAsync(prompt, null);
			if (extracted == null || string.IsNullOrWhiteSpace(extracted.Text)) return extracted;

			string trimmed = extracted.Text.Trim();
			if (trimmed.Contains("NO_CHANGES", StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogInformation("🧠 [Long Memory] Новых устойчивых фактов для @{User} не найдено (NO_CHANGES).", customer.Username);
				return extracted;
			}

			var bulletLines = trimmed
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.StartsWith("-") || l.StartsWith("•") || l.StartsWith("*"))
				.Select(l => "- " + l.TrimStart('-', '•', '*', ' '))
				.Distinct()
				.Take(15)
				.ToList();

			if (bulletLines.Count == 0) return extracted;

			string updatedBullets = string.Join("\n", bulletLines);
			string profilePart = ExtractProfileSection(
				!string.IsNullOrWhiteSpace(customer.AiContext) ? customer.AiContext : fallbackProfileContext);

			string mergedContext = CombineAiContext(profilePart, updatedBullets, existingMediaStats);

			customer.AiContext = mergedContext;
			await _db.SaveChangesAsync();

			await _redis.StringSetAsync(contextRedisKey, mergedContext, TimeSpan.FromHours(24));

			await _console.Log($"🧠 [Long Memory] Обновлена долгая память @{customer.Username} (фактов: {bulletLines.Count}).");
			return extracted;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "⚠️ [Long Memory] Не удалось обновить долгую память для @{User}", customer.Username);
			return null;
		}
	}

	public async Task SendLongMessageAsHumanAsync(string userId, string fullText, string token)
	{
		var chunks = SplitMessageIntoHumanChunks(fullText, 100);

		for (int i = 0; i < chunks.Count; i++)
		{
			await _instaService.SetTypingStatusAsync(userId, token);

			var chunk = chunks[i];
			int typingTime = Math.Clamp(chunk.Length * 90, 2000, 6000);
			await Task.Delay(typingTime);

			await _instaService.SendMessageAsync(userId, chunk, token);

			if (i < chunks.Count - 1)
			{
				await Task.Delay(Random.Shared.Next(1000, 2000));
			}
		}
	}

	private List<string> SplitMessageIntoHumanChunks(string text, int maxChunkLength)
	{
		var chunks = new List<string>();
		if (string.IsNullOrEmpty(text)) return chunks;

		var paragraphs = text.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);

		foreach (var paragraph in paragraphs)
		{
			if (paragraph.Length <= maxChunkLength)
			{
				chunks.Add(paragraph.Trim());
				continue;
			}

			var sentences = Regex.Split(paragraph, @"(?<=[.!?])\s+");
			var currentChunk = "";

			foreach (var sentence in sentences)
			{
				if (currentChunk.Length + sentence.Length <= maxChunkLength)
				{
					currentChunk += (currentChunk.Length > 0 ? " " : "") + sentence;
				}
				else
				{
					if (!string.IsNullOrEmpty(currentChunk)) chunks.Add(currentChunk.Trim());
					currentChunk = sentence;
				}
			}

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
				if (mediaList.Any(m => !m.IsProcessed)) return null;
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