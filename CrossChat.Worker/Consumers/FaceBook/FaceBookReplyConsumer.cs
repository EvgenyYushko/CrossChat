using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FaceBookReplyConsumer : IConsumer<FacebookMessageReceived>
	{
		private readonly AppDbContext _db;
		private readonly IFaceBookService _faceBookService;
		private readonly IAiService _aiService;
		private readonly IFaceBookConsole _console;
		private readonly ILogger<FaceBookReplyConsumer> _logger;

		public FaceBookReplyConsumer(
			AppDbContext db,
			IFaceBookService faceBookService,
			IAiService aiService,
			IFaceBookConsole console,
			ILogger<FaceBookReplyConsumer> logger)
		{
			_db = db;
			_faceBookService = faceBookService;
			_aiService = aiService;
			_console = console;
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<FacebookMessageReceived> context)
		{
			var msg = context.Message;

			var bot = await _db.FacebookSettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive || !bot.IsDirectEnabled || string.IsNullOrEmpty(bot.PageAccessToken))
				return;

			int mode = bot.DirectReplyMode > 0 ? bot.DirectReplyMode : 2;
			string? replyText = null;

			try
			{
				await _console.Log($"Обработка ЛС от пользователя {msg.SenderId} в Facebook (Режим: {mode})", bot.UserId, bot.Id);

				// === 1. ТОЛЬКО ШАБЛОНЫ (Spintax + Очеловечивание) ===
				if (mode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
				}
				// === 2. ТОЛЬКО ИИ ===
				else if (mode == 1)
				{
					var prompt = $"{bot.SystemPrompt}\n\nПользователь написал в личные сообщения Messenger: '{msg.Text}'. Ответь вежливо и по делу.";
					replyText = await _aiService.GeminiRequest(prompt, null);
				}
				// === 3. КОМБИНИРОВАННЫЙ ===
				else if (mode == 3)
				{
					try
					{
						var prompt = $"{bot.SystemPrompt}\n\nПользователь написал в личные сообщения Messenger: '{msg.Text}'. Ответь вежливо и по делу.";
						replyText = await _aiService.GeminiRequest(prompt, null);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[Facebook Direct] Сбой ИИ для ЛС. Переключение на шаблон.");
					}

					if (string.IsNullOrWhiteSpace(replyText))
					{
						replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText)) return;

				// Отправляем ответ пользователю в Messenger
				bool sent = await _faceBookService.SendReplyAsync(msg.SenderId, replyText, bot.PageAccessToken);
				if (sent)
				{
					await _console.Log($"Ответили в ЛС пользователю {msg.SenderId}: «{replyText}»", bot.UserId, bot.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook Direct] Ошибка ответа в ЛС пользователю {Sender}", msg.SenderId);
			}
		}
	}
}