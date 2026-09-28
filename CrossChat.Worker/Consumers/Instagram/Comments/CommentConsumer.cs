using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Instagram.Comments
{
	public class CommentConsumer : IConsumer<InstagramCommentReceived>
	{
		private readonly ILogger<CommentConsumer> _logger;
		private readonly AppDbContext _db;
		private readonly IInstagramService _instaService;
		private readonly IAiService _aiService;
		private readonly IInstagramConsole _console;
		private readonly IDatabase _redis;

		public CommentConsumer(
			ILogger<CommentConsumer> logger,
			AppDbContext db,
			IInstagramService instaService,
			IAiService aiService,
			IConnectionMultiplexer redis,
			IInstagramConsole console)
		{
			_logger = logger;
			_db = db;
			_instaService = instaService;
			_aiService = aiService;
			_console = console;
			_redis = redis.GetDatabase();
		}

		public async Task Consume(ConsumeContext<InstagramCommentReceived> context)
		{
			var msg = context.Message;

			// 1. ЗАЩИТА ОТ ДУБЛЕЙ ВЕБХУКА: не обрабатывать один коммент дважды (24 часа)
			var lockKey = $"lock:ig_comment:{msg.CommentId}";
			if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromHours(24), When.NotExists))
			{
				_logger.LogInformation("[Comment] Комментарий {CommentId} уже обработан. Пропускаем дубль.", msg.CommentId);
				return;
			}

			// 2. ПРАВИЛО: НЕ БОЛЕЕ 1 ОТВЕТА ОДНОМУ ПОЛЬЗОВАТЕЛЮ ПОД ОДНИМ ПОСТОМ (24 часа)
			if (!string.IsNullOrEmpty(msg.MediaId))
			{
				var userPostKey = $"lock:ig_comment_user:{msg.MediaId}:{msg.Username}";
				if (!await _redis.StringSetAsync(userPostKey, "answered", TimeSpan.FromHours(24), When.NotExists))
				{
					_logger.LogInformation("[Comment] Мы уже отвечали пользователю @{User} под постом {MediaId}. Пропускаем.", msg.Username, msg.MediaId);
					return;
				}
			}

			// 3. Ищем настройки аккаунта
			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == msg.BusinessAccountId);

			if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken) || !settings.IsCommentsEnabled)
			{
				_logger.LogInformation("[Comment] Игнорируем коммент. Бот выключен или не настроен.");
				return;
			}

			int replyMode = settings.CommentReplyMode > 0 ? settings.CommentReplyMode : 2;
			string? replyText = null;

			try
			{
				await _console.Log($"Обработка комментария от @{msg.Username}: '{msg.Text}' (Режим: {replyMode})", settings.UserId, settings.Id);

				// === 1. ТОЛЬКО ШАБЛОНЫ (Spintax + Human Salt) ===
				if (replyMode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
					_logger.LogInformation("[Comment] Сформирован шаблонный ответ для @{User}", msg.Username);
				}
				// === 2. ТОЛЬКО ИИ ===
				else if (replyMode == 1)
				{
					replyText = await GenerateAiCommentReply(settings, msg);
				}
				// === 3. КОМБИНИРОВАННЫЙ ===
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

					if (string.IsNullOrWhiteSpace(replyText))
					{
						_logger.LogInformation("[Comment] Использован резервный шаблон для коммента {CommentId} из-за сбоя ИИ.", msg.CommentId);
						replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText))
				{
					_logger.LogWarning("[Comment] Не удалось сформировать текст ответа для {CommentId}", msg.CommentId);
					return;
				}

				// =========================================================================
				// 4. ОЧЕЛОВЕЧИВАНИЕ: ПАУЗА 20-35 СЕКУНД (ИМИТАЦИЯ ЧТЕНИЯ И НАБОРА)
				// =========================================================================
				int humanPauseSeconds = Random.Shared.Next(20, 36);
				_logger.LogInformation("[Comment] ⏳ Пауза {Sec}с перед отправкой ответа для @{User}...", humanPauseSeconds, msg.Username);
				await Task.Delay(TimeSpan.FromSeconds(humanPauseSeconds));

				// 5. Отправляем ответ в Инстаграм
				await _instaService.ReplyToCommentAsync(msg.CommentId, replyText, settings.AccessToken);
				await _console.Log($"✅ Ответили на комментарий @{msg.Username}: «{replyText}»", settings.UserId, settings.Id);
			}
			catch (Exception ex)
			{
				await _console.LogError($"Ошибка при ответе на коммент {msg.CommentId}: {ex.Message}", settings.UserId, settings.Id);
				throw; // Пробрасываем ошибку для ретрая в MassTransit
			}
		}

		private async Task<string?> GenerateAiCommentReply(InstagramSettings settings, InstagramCommentReceived msg)
		{
			var fullPrompt = settings.CommentPrompt ?? "";
			fullPrompt += $"\nYou are replying to a PUBLIC COMMENT on Instagram. User @{msg.Username} wrote: '{msg.Text}'. Reply politely, naturally, and concisely.";

			return await _aiService.GeminiRequest(fullPrompt, null);
		}
	}
}