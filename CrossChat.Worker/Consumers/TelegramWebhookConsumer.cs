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
        
        // 1. Сохраняем сообщение в Redis для истории
        await _redis.ListRightPushAsync($"tg_history:{chatId}", context.Message.Text);
        await _redis.KeyExpireAsync($"tg_history:{chatId}", TimeSpan.FromMinutes(10));

        // 2. Логика Debounce (как в Инстаграм)
        var lockKey = $"tg_debounce:{chatId}";
        if (await _redis.StringSetAsync(lockKey, "active", TimeSpan.FromSeconds(30), When.NotExists))
        {
            await context.SchedulePublish(TimeSpan.FromSeconds(30), new TelegramProcessReply 
            { 
                ChatId = chatId, 
                BotToken = context.Message.BotToken 
            });
        }
    }
}