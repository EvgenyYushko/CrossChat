using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.Instagram.Comments
{
	public class CommentConsumer : IConsumer<InstagramCommentReceived>
	{
		private readonly ILogger<CommentConsumer> _logger;
		private readonly AppDbContext _db;
		private readonly IInstagramService _instaService;
		private readonly IAiService _aiService;
		private readonly IInstagramConsole _console;

		private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
		{
			PermitLimit = 10,
			Window = TimeSpan.FromMinutes(1),
			QueueLimit = 0
		});

		public CommentConsumer(
			ILogger<CommentConsumer> logger, 
			AppDbContext db, 
			IInstagramService instaService, 
			IAiService aiService, 
			IInstagramConsole console)
		{
			_logger = logger;
			_db = db;
			_instaService = instaService;
			_aiService = aiService;
			_console = console;
		}

		public async Task Consume(ConsumeContext<InstagramCommentReceived> context)
		{
			using var lease = await _rateLimiter.AcquireAsync(1, context.CancellationToken);
			if (!lease.IsAcquired) throw new Exception("Rate limit exceeded (Comments).");

			var msg = context.Message;

			// 1. Ищем настройки пользователя
			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == msg.BusinessAccountId);

			if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken) || !settings.IsCommentsEnabled)
			{
				_logger.LogInformation("[Comment] Игнорируем коммент. Бот выключен или не настроен.");
				return;
			}

			// 1 = ИИ, 2 = Шаблоны (дефолт), 3 = Комбинированный
			int replyMode = settings.CommentReplyMode > 0 ? settings.CommentReplyMode : 2;
			string? replyText = null;

			try
			{
				// === СЦЕНАРИЙ 2: ТОЛЬКО ШАБЛОНЫ (0 расхода токенов ИИ) ===
				if (replyMode == 2)
				{
					replyText = GetRandomTemplate(settings.CommentTemplates);
					_logger.LogInformation("[Comment] Сформирован шаблонный ответ для @{User}", msg.Username);
				}
				// === СЦЕНАРИЙ 1: ТОЛЬКО ИИ ===
				else if (replyMode == 1)
				{
					replyText = await GenerateAiCommentReply(settings, msg);
				}
				// === СЦЕНАРИЙ 3: КОМБИНИРОВАННЫЙ (ИИ + РЕЗЕРВНЫЙ ШАБЛОН) ===
				else if (replyMode == 3)
				{
					try
					{
						replyText = await GenerateAiCommentReply(settings, msg);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[Comment] Сбой генерации ИИ для коммента {CommentId}. Переход на резервный шаблон.", msg.CommentId);
					}

					// Если у ИИ кончились кредиты или он вернул пустоту — берем шаблон!
					if (string.IsNullOrWhiteSpace(replyText))
					{
						_logger.LogInformation("[Comment] Использован резервный шаблон для коммента {CommentId} из-за сбоя ИИ.", msg.CommentId);
						replyText = GetRandomTemplate(settings.CommentTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText))
				{
					_logger.LogWarning("[Comment] Не удалось сформировать текст ответа (шаблоны пусты) для {CommentId}", msg.CommentId);
					return;
				}

				// 4. Отправляем ответ в Инстаграм
				await _instaService.ReplyToCommentAsync(msg.CommentId, replyText, settings.AccessToken);
				await _console.Log($"Ответ на комментарий @{msg.Username}: «{replyText}»", settings.UserId, settings.Id);
			}
			catch (Exception ex)
			{
				await _console.LogError($"Ошибка при ответе на коммент {msg.CommentId}: {ex.Message}", settings.UserId, settings.Id);
				throw; // Бросаем только сетевые ошибки самого Инстаграма, чтобы MassTransit повторил
			}
		}

		private async Task<string?> GenerateAiCommentReply(InstagramSettings settings, InstagramCommentReceived msg)
		{
			var fullPrompt = settings.CommentPrompt ?? "";
			fullPrompt += $"\nYou are now replying to a PUBLIC COMMENT under your post. The user @{msg.Username} wrote: '{msg.Text}'. Reply politely and concisely.";
			
			return await _aiService.GeminiRequest(fullPrompt, null);
		}

		/// <summary>
		/// Выбирает случайную фразу из шаблонов (строки через Enter или JSON-массив)
		/// </summary>
		private static string? GetRandomTemplate(string? rawTemplates)
		{
			if (string.IsNullOrWhiteSpace(rawTemplates)) return null;

			// Если в формате JSON-массива: ["Привет", "Спасибо"]
			if (rawTemplates.TrimStart().StartsWith("["))
			{
				try
				{
					var list = JsonSerializer.Deserialize<List<string>>(rawTemplates);
					if (list != null && list.Any())
					{
						return list[Random.Shared.Next(list.Count)].Trim();
					}
				}
				catch { }
			}

			// Если каждая фраза с новой строки
			var lines = rawTemplates
				.Split(new[] { "\r\n", "\r", "\n", "|" }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => !string.IsNullOrEmpty(l))
				.ToList();

			if (lines.Any())
			{
				return lines[Random.Shared.Next(lines.Count)];
			}

			return rawTemplates.Trim();
		}
	}
}