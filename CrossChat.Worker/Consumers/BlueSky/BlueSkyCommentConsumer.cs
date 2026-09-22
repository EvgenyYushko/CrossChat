using System;
using System.Threading.Tasks;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyCommentConsumer : IConsumer<BlueSkyCommentReceived>
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IAiService _aiService;
		private readonly IBlueSkyConsole _console;
		private readonly ILogger<BlueSkyCommentConsumer> _logger;

		public BlueSkyCommentConsumer(
			AppDbContext db, 
			IBlueSkyService bskyService, 
			IAiService aiService, 
			IBlueSkyConsole console, 
			ILogger<BlueSkyCommentConsumer> logger)
		{
			_db = db;
			_bskyService = bskyService;
			_aiService = aiService;
			_console = console;
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<BlueSkyCommentReceived> context)
		{
			var msg = context.Message;

			var bot = await _db.BlueSkySettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive || !bot.IsCommentsEnabled || string.IsNullOrEmpty(bot.AccessToken)) return;

			var botModel = new BlueSkyModel
			{
				AccessToken = bot.AccessToken,
				RefreshToken = bot.RefreshToken,
				Handle = bot.Handle,
				PrivateKeyJson = bot.PrivateKeyJson,
				TokenExpiresAt = bot.TokenExpiresAt,
				Did = bot.Did,
				PdsUrl = bot.PdsUrl
			};

			int replyMode = bot.CommentReplyMode > 0 ? bot.CommentReplyMode : 2;
			string? replyText = null;

			try
			{
				await _console.Log($"Обработка комментария от @{msg.AuthorHandle} (Режим: {replyMode})", bot.UserId, bot.Id);

				// === 1. ТОЛЬКО ШАБЛОНЫ (Spintax) ===
				if (replyMode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(bot.CommentTemplates);
				}
				// === 2. ТОЛЬКО ИИ ===
				else if (replyMode == 1)
				{
					var prompt = $"{bot.CommentPrompt}\n\nПользователь @{msg.AuthorHandle} оставил комментарий в BlueSky: '{msg.Text}'. Ответь кратко.";
					replyText = await _aiService.GeminiRequest(prompt, null);
				}
				// === 3. КОМБИНИРОВАННЫЙ ===
				else if (replyMode == 3)
				{
					try
					{
						var prompt = $"{bot.CommentPrompt}\n\nПользователь @{msg.AuthorHandle} оставил комментарий в BlueSky: '{msg.Text}'. Ответь кратко.";
						replyText = await _aiService.GeminiRequest(prompt, null);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[BlueSky] Сбой ИИ, переключение на резервный шаблон для @{User}", msg.AuthorHandle);
					}

					if (string.IsNullOrWhiteSpace(replyText))
					{
						replyText = InstagramCommentEngine.GetRandomTemplate(bot.CommentTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText)) return;

				// Отправляем ответ в ветку
				bool success = await _bskyService.ReplyToThreadCommentAsync(
					replyText, 
					msg.CommentUri, 
					msg.CommentCid, 
					msg.RootUri, 
					msg.RootCid, 
					botModel);

				if (success)
				{
					await _console.Log($"Ответили на комментарий @{msg.AuthorHandle}: «{replyText}»", bot.UserId, bot.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка ответа на комментарий @{User}", msg.AuthorHandle);
			}
		}
	}
}