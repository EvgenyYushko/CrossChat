using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FaceBookReplyConsumer : IConsumer<FaceBookProcessReply>
	{
		private readonly AppDbContext _db;
		private readonly IFaceBookService _faceBookService;
		private readonly ILogger<FaceBookReplyConsumer> _logger;
		private readonly IDatabase _redis;

		public FaceBookReplyConsumer(AppDbContext db, IFaceBookService faceBookService, ILogger<FaceBookReplyConsumer> logger, IConnectionMultiplexer redis)
		{
			_db = db;
			_faceBookService = faceBookService;
			_logger = logger;
			_redis = redis.GetDatabase();
		}

		public async Task Consume(ConsumeContext<FaceBookProcessReply> context)
		{
			var msg = context.Message;
			try
			{
				var bot = await _db.FacebookSettings.FindAsync(msg.BotDbId);
				if (bot == null || !bot.IsActive) return;

				var dlg = await _faceBookService.GetDialogByIdAsync(bot.PageAccessToken, msg.DialogId);
				if (dlg == null || !dlg.messages.data.Any()) return;

				var messages = dlg.messages.data;

				var chatHistory = messages.Select(m => new AiRequest
				{
					Role = m.from.id == bot.PageId.ToString() ? "model" : "user",
					Text = m.message
				}).ToList();

				//var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);
				var aiResponse = "hello";

				if (!string.IsNullOrWhiteSpace(aiResponse))
				{
					// 5. Отправка ответа
					var recepientId = messages.First().from.id;
					var sended = await _faceBookService.SendReplyAsync(recepientId, aiResponse, bot.PageAccessToken);
					if (sended)
					{
						_logger.LogInformation($"[FaceBookWorker] ✅ Ответили в чат {msg.DialogId}");
					}
				}
			}
			finally
			{
				await _redis.KeyDeleteAsync($"lock:fsbk_queued:{msg.DialogId}");
			}
		}
	}
}
