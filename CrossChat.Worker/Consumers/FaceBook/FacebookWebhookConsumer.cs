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

		// Базовое окно тишины после КАЖДОГО сообщения (20 секунд)
		private const int DebounceSeconds = 20;

		public FacebookWebhookConsumer(ILogger<FacebookWebhookConsumer> logger, IConnectionMultiplexer redisMux)
		{
			_logger = logger;
			_redis = redisMux.GetDatabase();
		}

		public async Task Consume(ConsumeContext<FacebookMessageReceived> context)
		{
			var senderId = context.Message.SenderId;
			var pageId = context.Message.PageId;

			var targetTimeKey = $"debounce:target_time:fb:{senderId}:{pageId}";
			var activeTimerKey = $"debounce:timer_active:fb:{senderId}:{pageId}";

			int extensionTime = (context.Message.AttachmentCount * 10);
			int totalWait = DebounceSeconds + extensionTime;

			// 1. Сдвигаем целевое время ответа вперед (от текущего момента + 20 секунд)
			var targetTimeUtc = DateTime.UtcNow.AddSeconds(totalWait);
			await _redis.StringSetAsync(targetTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromMinutes(10));

			// 2. Проверяем, запущен ли уже таймер ожидания в RabbitMQ
			bool isFirstMessage = await _redis.StringSetAsync(activeTimerKey, "1", TimeSpan.FromMinutes(10), When.NotExists);

			if (isFirstMessage)
			{
				_logger.LogInformation("[Facebook Debounce] ⏳ Первое сообщение от {Sender}. Запущен таймер тишины {Sec}с...", senderId, totalWait);

				// Планируем проверку через 20 секунд
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