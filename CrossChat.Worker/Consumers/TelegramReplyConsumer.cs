using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers;

public class TelegramReplyConsumer : IConsumer<TelegramProcessReply>
{
	private readonly ILogger<TelegramReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly ITelegramService _telegramService;
	private readonly IAiService _aiService;
	private readonly IDatabase _redis;

	public TelegramReplyConsumer(ILogger<TelegramReplyConsumer> logger, AppDbContext db,
		ITelegramService telegramService, IAiService aiService, IConnectionMultiplexer redis)
	{
		_logger = logger; _db = db;
		_telegramService = telegramService; _aiService = aiService;
		_redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<TelegramProcessReply> context)
	{
		_logger.LogInformation("пришло  ообщение");

		var chatId = context.Message.ChatId;
		var token = context.Message.BotToken;
		var key = $"tg_history:{chatId}";

		// 1. Ищем настройки в БД
		var settings = await _db.TelegramSettings.FirstOrDefaultAsync(s => s.BotToken == token);
		if (settings == null || !settings.IsActive) return;

		// 2. Получаем историю, но НЕ удаляем её из Redis
		var rawMessages = await _redis.ListRangeAsync(key, -15, -1);
		if (rawMessages == null || rawMessages.Length == 0) return;

		var chatHistory = rawMessages.Select(v => new AiRequest
		{
			Role = "user",
			Text = string.IsNullOrEmpty(v.ToString()) ? "[Пустое сообщение]" : v.ToString()
		}).ToList();

		// 3. Запрос к ИИ
		var answer = await _aiService.GetAnswerAsync(settings.SystemPrompt, chatHistory, null);

		// 4. Отправка ответа
		// Если здесь возникнет ошибка, метод выбросит исключение, 
		// сообщение вернется в очередь RabbitMQ, а Redis останется нетронутым!
		await _telegramService.SendMessageAsync(token, chatId, answer);

		// 5. Успех! Теперь чистим Redis
		await _redis.KeyDeleteAsync(key);

		_logger.LogInformation($"[Reply] Успешно ответили пользователю {chatId} и очистили историю.");
	}
}