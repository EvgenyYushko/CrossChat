using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyReplyConsumer : IConsumer<BlueSkyProcessReply>
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IAiService _aiService;
		private readonly IDatabase _redis;
		private readonly ILogger<BlueSkyReplyConsumer> _logger;

		public BlueSkyReplyConsumer(AppDbContext db, IBlueSkyService bskyService, IAiService aiService, 
			IConnectionMultiplexer redis, ILogger<BlueSkyReplyConsumer> logger)
		{
			_db = db; _bskyService = bskyService; _aiService = aiService;
			_redis = redis.GetDatabase(); _logger = logger;
		}

		public async Task Consume(ConsumeContext<BlueSkyProcessReply> context)
		{
			var msg = context.Message;

			// 1. Достаем настройки бота из БД
			var bot = await _db.BlueSkySettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive) return;

			try
			{
				var botModel = new BlueSkyModel()
				{
					AccessToken = bot.AccessToken,
					RefreshToken = bot.RefreshToken,
					Handle = bot.Handle,
					PrivateKeyJson = bot.PrivateKeyJson,
					TokenExpiresAt = bot.TokenExpiresAt,
					Did = bot.Did,
					PdsUrl = bot.PdsUrl,
					SystemPrompt = bot.SystemPrompt
				};

				// 2. Получаем историю сообщений (контекст)
				var messages = await _bskyService.GetMessagesAsync(botModel, msg.ConvoId, 10);
				if (messages == null || !messages.Any()) return;

				// Двойная проверка: не ответили ли мы уже (пока сообщение висело в очереди)
				if (messages.Last().Sender.Did == botModel.Did) return;

				// 3. Формируем историю для ИИ
				var chatHistory = messages.Select(m => new AiRequest
				{
					Role = m.Sender.Did == botModel.Did ? "model" : "user",
					Text = m.Text
				}).ToList();

				// 4. Запрос к ИИ
				//var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);
				var aiResponse = "hello";

				if (!string.IsNullOrWhiteSpace(aiResponse))
				{
					// 5. Отправка ответа
					var sended = await _bskyService.SendChatMessageAsync(botModel, msg.ConvoId, aiResponse);
					if (sended)
					{
						// 6. Помечаем прочитанным
						await _bskyService.MarkConvoAsReadAsync(botModel, msg.ConvoId, messages.Last().Id);
						_logger.LogInformation($"[BlueSkyWorker] ✅ Ответили в чат {msg.ConvoId}");
					}
				}
			}
			finally
			{
				// ВАЖНО: Удаляем Redis-замок, чтобы при следующем сканировании чат снова мог попасть в очередь
				await _redis.KeyDeleteAsync($"lock:bsky_queued:{msg.ConvoId}");
			}
		}
	}
}
