using System;
using System.Threading.Tasks;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Facebook.Comments
{
	public class FacebookCommentConsumer : IConsumer<FacebookCommentReceived>
	{
		private readonly AppDbContext _db;
		private readonly IFaceBookService _fbService;
		private readonly IAiService _aiService;
		private readonly IFaceBookConsole _console;
		private readonly IDatabase _redis;
		private readonly ILogger<FacebookCommentConsumer> _logger;

		public FacebookCommentConsumer(
			AppDbContext db,
			IFaceBookService fbService,
			IAiService aiService,
			IFaceBookConsole console,
			IConnectionMultiplexer redis,
			ILogger<FacebookCommentConsumer> logger)
		{
			_db = db;
			_fbService = fbService;
			_aiService = aiService;
			_console = console;
			_redis = redis.GetDatabase();
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<FacebookCommentReceived> context)
		{
			var msg = context.Message;

			// 1. Защита от дублей вебхуков через Redis (на 2 часа)
			var lockKey = $"lock:fb_comment:{msg.CommentId}";
			if (!await _redis.StringSetAsync(lockKey, "1", TimeSpan.FromHours(2), When.NotExists))
			{
				_logger.LogInformation("[Facebook Consumer] Комментарий {Id} уже обработан. Пропуск.", msg.CommentId);
				return;
			}

			// 2. Ищем настройки страницы в БД
			var settings = await _db.FacebookSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.PageId == msg.PageId);

			if (settings == null || !settings.IsCommentsEnabled || string.IsNullOrEmpty(settings.PageAccessToken))
			{
				_logger.LogInformation("[Facebook Consumer] Страница {PageId} отключена или автоответы на комментарии выключены.", msg.PageId);
				return;
			}

			int mode = settings.CommentReplyMode > 0 ? settings.CommentReplyMode : 2;
			string? replyText = null;

			try
			{
				await _console.Log($"Обработка комментария от {msg.SenderName} на Facebook: '{msg.Text}' (Режим: {mode})", settings.UserId, settings.Id);

				// === 1. ТОЛЬКО ШАБЛОНЫ (Spintax + Human Salt) ===
				if (mode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
				}
				// === 2. ТОЛЬКО ИИ ===
				else if (mode == 1)
				{
					replyText = await GenerateAiReply(settings, msg);
				}
				// === 3. КОМБИНИРОВАННЫЙ (ИИ ➔ при сбое Шаблон) ===
				else if (mode == 3)
				{
					try
					{
						replyText = await GenerateAiReply(settings, msg);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[Facebook Consumer] Сбой ИИ для комментария {Id}. Переход на резервный шаблон.", msg.CommentId);
					}

					if (string.IsNullOrWhiteSpace(replyText))
					{
						replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText))
				{
					_logger.LogWarning("[Facebook Consumer] Шаблоны пусты, ответ отменен для {Id}", msg.CommentId);
					return;
				}

				// 3. Отправляем ответ в ветку комментария
				bool success = await _fbService.ReplyToCommentAsync(msg.CommentId, replyText, settings.PageAccessToken);

				if (success)
				{
					await _console.Log($"Ответили на комментарий {msg.SenderName} в Facebook: «{replyText}»", settings.UserId, settings.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook Consumer] Ошибка обработки комментария {Id}", msg.CommentId);
			}
		}

		private async Task<string?> GenerateAiReply(FacebookSettings settings, FacebookCommentReceived msg)
		{
			var prompt = $"{settings.CommentPrompt}\n\nПользователь {msg.SenderName} оставил комментарий к публикации на странице Facebook: '{msg.Text}'. Ответь вежливо, естественно и кратко.";
			return await _aiService.GeminiRequest(prompt, null);
		}
	}
}