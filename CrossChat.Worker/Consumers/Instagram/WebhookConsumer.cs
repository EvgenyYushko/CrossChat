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
		var lockKey = $"debounce:{senderId}:{recipientId}";

		// ВАЖНО: Проверяем, пришло ли медиа
		int extensionTime = (context.Message.AttachmentCount * 15) + 5;

		// Пытаемся взять текущий TTL ключа
		var ttl = await _redis.KeyTimeToLiveAsync(lockKey);

		if (ttl.HasValue)
		{
			// Таймер уже идет! Продлеваем его
			var newTtl = ttl.Value.TotalSeconds + extensionTime;
			await _redis.KeyExpireAsync(lockKey, TimeSpan.FromSeconds(newTtl));

			_logger.LogInformation($"[Debounce] Продлили таймер для {senderId} на {extensionTime} сек.");
			// Сообщение сохраняем в БД, но задачу в RabbitMQ НЕ планируем (она уже есть)
		}
		else
		{
			// Таймера нет, создаем новый
			await _redis.StringSetAsync(lockKey, "active", TimeSpan.FromSeconds(30 + extensionTime));

			await context.SchedulePublish(TimeSpan.FromSeconds(30 + extensionTime), new ProcessDialogReply
			{
				SenderId = senderId,
				RecipientId = recipientId,
				ReplyId = context.Message.MessageId
			});
		}
	}
}
