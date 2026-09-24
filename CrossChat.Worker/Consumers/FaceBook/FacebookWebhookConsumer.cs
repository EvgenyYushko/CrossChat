using System;
using System.Threading.Tasks;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FacebookWebhookConsumer : IConsumer<FacebookMessageReceived>
	{
		private readonly ILogger<FacebookWebhookConsumer> _logger;
		private readonly IDatabase _redis;

		public FacebookWebhookConsumer(ILogger<FacebookWebhookConsumer> logger, IConnectionMultiplexer redisMux)
		{
			_logger = logger;
			_redis = redisMux.GetDatabase();
		}

		public async Task Consume(ConsumeContext<FacebookMessageReceived> context)
		{
			var senderId = context.Message.SenderId;
			var pageId = context.Message.PageId;
			var lockKey = $"debounce:fb:{senderId}:{pageId}";

			// Если прикрепили вложения — даем больше времени
			int extensionTime = (context.Message.AttachmentCount * 10) + 5;

			var ttl = await _redis.KeyTimeToLiveAsync(lockKey);

			if (ttl.HasValue)
			{
				// Таймер уже идет! Пользователь строчит следующее сообщение — продлеваем окно ожидания!
				var newTtl = ttl.Value.TotalSeconds + extensionTime;
				await _redis.KeyExpireAsync(lockKey, TimeSpan.FromSeconds(newTtl));

				_logger.LogInformation($"[Facebook Debounce] Продлили таймер для {senderId} на {extensionTime} сек.");
			}
			else
			{
				// Первое сообщение: запускаем окно ожидания 30 секунд
				await _redis.StringSetAsync(lockKey, "active", TimeSpan.FromSeconds(30 + extensionTime));

				_logger.LogInformation($"[Facebook Debounce] Запущен таймер 30с для {senderId}. Ждем окончания мысли...");

				// Планируем отправку ответа ровно через 30 секунд тишины!
				await context.SchedulePublish(TimeSpan.FromSeconds(30 + extensionTime), new ProcessFacebookDialogReply
				{
					PageId = pageId,
					SenderId = senderId,
					ReplyId = context.Message.MessageId
				});
			}
		}
	}
}