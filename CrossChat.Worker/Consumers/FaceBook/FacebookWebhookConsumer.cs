using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FacebookWebhookConsumer : IConsumer<FacebookMessageReceived>
	{
		private readonly ILogger<FacebookWebhookConsumer> _logger;
		private readonly IDatabase _redis;
		private readonly AppDbContext _db;
		private readonly IInboxService _inboxService;

		// Окно тишины (20 секунд)
		private const int DebounceSeconds = 20;

		public FacebookWebhookConsumer(
			ILogger<FacebookWebhookConsumer> logger,
			IConnectionMultiplexer redisMux,
			AppDbContext db,
			IInboxService inboxService)
		{
			_logger = logger;
			_redis = redisMux.GetDatabase();
			_db = db;
			_inboxService = inboxService;
		}

		public async Task Consume(ConsumeContext<FacebookMessageReceived> context)
		{
			var senderId = context.Message.SenderId;
			var pageId = context.Message.PageId;
			var messageText = context.Message.Text ?? "[Сообщение]";

			// ==================================================================================
			// 1. МГНОВЕННЫЙ ПУШ В ИНБОКС (МЕНЕДЖЕР ВИДИТ СООБЩЕНИЕ ЧЕРЕЗ 0.1 СЕКУНДЫ!)
			// ==================================================================================
			try
			{
				var settings = await _db.FacebookSettings
					.AsNoTracking()
					.FirstOrDefaultAsync(s => s.PageId == pageId);

				if (settings != null)
				{
					int networkTypeFb = (int)NetworkType.Facebook;
					var conversation = await _inboxService.GetOrCreateConversationAsync(
						settings.ProfileId,
						networkTypeFb,
						settings.Id,
						senderId,
						"Пользователь Facebook",
						"Пользователь Facebook",
						null);

					// Сохраняем в БД и шлем SignalR ПРЯМО СЕЙЧАС!
					await _inboxService.SaveIncomingMessageAsync(
						conversation.Id,
						context.Message.MessageId,
						messageText,
						context.Message.MediaUrl,
						context.Message.MediaType);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "[Facebook Inbox] Ошибка моментального сохранения сообщения в инбокс");
			}

			// ==================================================================================
			// 2. ДЕБАУНС ДЛЯ ИИ-БОТА (БОТ СПОКОЙНО ЖДЕТ ТИШИНЫ)
			// ==================================================================================
			var targetTimeKey = $"debounce:target_time:fb:{senderId}:{pageId}";
			var activeTimerKey = $"debounce:timer_active:fb:{senderId}:{pageId}";

			int extensionTime = (context.Message.AttachmentCount * 10);
			int totalWait = DebounceSeconds + extensionTime;

			var targetTimeUtc = DateTime.UtcNow.AddSeconds(totalWait);
			await _redis.StringSetAsync(targetTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromMinutes(10));

			bool isFirstMessage = await _redis.StringSetAsync(activeTimerKey, "1", TimeSpan.FromMinutes(10), When.NotExists);

			if (isFirstMessage)
			{
				_logger.LogInformation("[Facebook Debounce] ⏳ Первое сообщение от {Sender}. Запущен таймер тишины {Sec}с...", senderId, totalWait);

				await context.SchedulePublish(TimeSpan.FromSeconds(totalWait), new ProcessFacebookDialogReply
				{
					PageId = pageId,
					SenderId = senderId,
					ReplyId = context.Message.MessageId
				});
			}
			else
			{
				_logger.LogInformation("[Facebook Debounce] 💬 Новое сообщение от {Sender}! Таймер сдвинут вперед на {Sec}с.", senderId, totalWait);
			}
		}
	}
}