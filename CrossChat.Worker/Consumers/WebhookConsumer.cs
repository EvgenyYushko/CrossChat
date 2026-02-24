using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers;

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
		_logger.LogInformation($"[Consume] Начало метода");

		var message = context.Message;
		var senderId = message.SenderId;
		var recipientId = message.RecipientId;

		// Ключ блокировки: "lock:dialog_id"
		var lockKey = $"debounce:{senderId}";

		// Пытаемся установить ключ в Redis.
		// StringSetAsync вернет TRUE, только если ключа НЕ существовало (When.NotExists).
		// Ключ сам исчезнет через 30 секунд (Expiry).
		bool isFirstMessage = await _redis.StringSetAsync(
			lockKey,
			"active",
			TimeSpan.FromSeconds(30),
			When.NotExists
		);

		if (isFirstMessage)
		{
			_logger.LogInformation($"[Debounce] Первое сообщение от {senderId}. Запускаем таймер на 30 сек.");

			// ИСПОЛЬЗУЕМ SchedulePublish ВМЕСТО ScheduleSend
			// Нам не нужно знать URI очереди. MassTransit сам найдет ReplyConsumer.
			await context.SchedulePublish(
				TimeSpan.FromSeconds(30),
				new ProcessDialogReply { SenderId = senderId, RecipientId = recipientId });
		}
		else
		{
			_logger.LogInformation($"[Debounce] Сообщение от {senderId} пропущено (таймер уже идет).");
		}
	}
}
