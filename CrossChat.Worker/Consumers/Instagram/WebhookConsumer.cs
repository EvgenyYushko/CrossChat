using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Instagram;

public class WebhookConsumer : IConsumer<InstagramMessageReceived>
{
	private readonly ILogger<WebhookConsumer> _logger;
	private readonly IDatabase _redis;

	public WebhookConsumer(ILogger<WebhookConsumer> logger, IConnectionMultiplexer redisMux)
	{
		_logger = logger;
		_redis = redisMux.GetDatabase();
	}

	public async Task Consume(ConsumeContext<InstagramMessageReceived> context)
	{
		var senderId = context.Message.SenderId;
		var recipientId = context.Message.RecipientId;

		var targetTimeKey = $"debounce:target_time:ig:{senderId}:{recipientId}";
		var activeTimerKey = $"debounce:timer_active:ig:{senderId}:{recipientId}";

		int extensionTime = (context.Message.AttachmentCount * 10);
		int totalWait = 25 + extensionTime; // 25 секунд тишины

		// 1. Сдвигаем целевое время вперед
		var targetTimeUtc = DateTime.UtcNow.AddSeconds(totalWait);
		await _redis.StringSetAsync(targetTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromMinutes(10));

		// 2. Запускаем таймер, только если это первое сообщение в серии
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
