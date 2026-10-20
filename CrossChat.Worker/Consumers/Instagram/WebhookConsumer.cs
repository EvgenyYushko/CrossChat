using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Instagram;

public class WebhookConsumer : IConsumer<InstagramMessageReceived>
{
	private readonly ILogger<WebhookConsumer> _logger;
	private readonly IDatabase _redis;
	private readonly AppDbContext _db;
	private readonly IInboxService _inboxService;

	public WebhookConsumer(
		ILogger<WebhookConsumer> logger, 
		IConnectionMultiplexer redisMux,
		AppDbContext db,
		IInboxService inboxService)
	{
		_logger = logger;
		_redis = redisMux.GetDatabase();
		_db = db;
		_inboxService = inboxService;
	}

	public async Task Consume(ConsumeContext<InstagramMessageReceived> context)
	{
		var senderId = context.Message.SenderId;
		var recipientId = context.Message.RecipientId;
		var messageText = context.Message.Text ?? "[Сообщение]";

		// ==================================================================================
		// 1. МГНОВЕННЫЙ ПУШ В ИНБОКС (МЕНЕДЖЕР ВИДИТ СООБЩЕНИЕ ЧЕРЕЗ 0.1 СЕКУНДЫ!)
		// ==================================================================================
		try
		{
			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == recipientId);

			if (settings != null)
			{
				var customer = await _db.InstagramBotCustomers
					.AsNoTracking()
					.FirstOrDefaultAsync(c => c.InstagramSettingsId == settings.Id && c.InstagramSenderId == senderId);

				int networkTypeInstagram = 1;
				var conversation = await _inboxService.GetOrCreateConversationAsync(
					settings.ProfileId,
					networkTypeInstagram,
					settings.Id,
					senderId,
					customer?.Username ?? "instagram_user",
					customer?.Username ?? "instagram_user",
					customer?.ProfilePictureUrl);

				// Сохраняем и шлем SignalR ПРЯМО СЕЙЧАС!
				await _inboxService.SaveIncomingMessageAsync(
					conversation.Id,
					context.Message.MessageId,
					messageText,
					context.Message.MediaUrl,   // <-- 4-й параметр
					context.Message.MediaType); // <-- 5-й параметр
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "[Inbox] Ошибка моментального сохранения сообщения в инбокс");
		}

		// ==================================================================================
		// 2. ДЕБАУНС ДЛЯ ИИ-БОТА (БОТ СПОКОЙНО ЖДЕТ 25 СЕКУНД ТИШИНЫ)
		// ==================================================================================
		var targetTimeKey = $"debounce:target_time:ig:{senderId}:{recipientId}";
		var activeTimerKey = $"debounce:timer_active:ig:{senderId}:{recipientId}";

		int extensionTime = (context.Message.AttachmentCount * 10);
		int totalWait = 25 + extensionTime;

		var targetTimeUtc = DateTime.UtcNow.AddSeconds(totalWait);
		await _redis.StringSetAsync(targetTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromMinutes(10));

		bool isFirst = await _redis.StringSetAsync(activeTimerKey, "1", TimeSpan.FromMinutes(10), When.NotExists);

		if (isFirst)
		{
			_logger.LogInformation($"[Instagram Debounce] ⏳ Первое сообщение от {senderId}. Запущен таймер тишины {totalWait}с...");

			await context.SchedulePublish(TimeSpan.FromSeconds(totalWait), new ProcessDialogReply
			{
				SenderId = senderId,
				RecipientId = recipientId,
				ReplyId = context.Message.MessageId
			});
		}
		else
		{
			_logger.LogInformation($"[Instagram Debounce] 💬 Новое сообщение от {senderId}. Таймер сдвинут вперед на {totalWait}с.");
		}
	}
}