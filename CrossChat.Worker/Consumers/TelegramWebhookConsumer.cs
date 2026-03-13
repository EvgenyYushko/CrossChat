using CrossChat.Worker.Contracts;
using MassTransit;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers;

public class TelegramWebhookConsumer : IConsumer<TelegramMessageReceived>
{
	private readonly IDatabase _redis;

	public TelegramWebhookConsumer(IConnectionMultiplexer redisMux)
	{
		_redis = redisMux.GetDatabase();
	}

	public async Task Consume(ConsumeContext<TelegramMessageReceived> context)
	{
		Console.WriteLine("пришло  ообщение на ответ");

		var chatId = context.Message.ChatId;
		var token = context.Message.BotToken;

		var historyKey = $"tg_history:{token}:{chatId}";
		var debounceKey = $"tg_debounce:{token}:{chatId}";

		// 1. Сохраняем сообщение
		await _redis.ListRightPushAsync(historyKey, context.Message.Text);
		await _redis.KeyExpireAsync(historyKey, TimeSpan.FromMinutes(10));

		// 2. Логика Debounce
		if (await _redis.StringSetAsync(debounceKey, "active", TimeSpan.FromSeconds(30), When.NotExists))
		{
			await context.SchedulePublish(TimeSpan.FromSeconds(10), new TelegramProcessReply
			{
				ChatId = chatId,
				BotToken = token
			});
		}
	}
}