using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyCommentConsumer : IConsumer<BlueSkyCommentReceived>
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IAiService _aiService;
		private readonly IBlueSkyConsole _console;
		private readonly IDatabase _redis;
		private readonly ILogger<BlueSkyCommentConsumer> _logger;

		public BlueSkyCommentConsumer(
			AppDbContext db,
			IBlueSkyService bskyService,
			IAiService aiService,
			IBlueSkyConsole console,
			IConnectionMultiplexer redis,
			ILogger<BlueSkyCommentConsumer> logger)
		{
			_db = db;
			_bskyService = bskyService;
			_aiService = aiService;
			_console = console;
			_redis = redis.GetDatabase();
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<BlueSkyCommentReceived> context)
		{
			var msg = context.Message;

			// 1. ЗАЩИТА ОТ ДУБЛЕЙ: не обрабатывать один и тот же комментарий дважды (храним 24 часа)
			var lockKey = $"lock:bsky_comment:{msg.CommentCid}";
			if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromHours(24), When.NotExists))
			{
				_logger.LogInformation("[BlueSky] Комментарий {Cid} уже обработан ранее. Пропускаем дубль.", msg.CommentCid);
				return;
			}

			// 2. ЗАЩИТА ОТ СПАМА: не более 1 ответа пользователю в одной ветке (на 24 часа)
			if (!string.IsNullOrEmpty(msg.RootCid) && !string.IsNullOrEmpty(msg.AuthorDid))
			{
				var userThreadKey = $"lock:bsky_answered_user:{msg.RootCid}:{msg.AuthorDid}";
				if (!await _redis.StringSetAsync(userThreadKey, "answered", TimeSpan.FromHours(24), When.NotExists))
				{
					_logger.LogInformation("[BlueSky] Мы уже отвечали пользователю {Author} в ветке {Root}. Пропускаем.", msg.AuthorHandle, msg.RootCid);
					return;
				}
			}

			// 3. Загружаем настройки бота
			var bot = await _db.BlueSkySettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive || !bot.IsCommentsEnabled || string.IsNullOrEmpty(bot.AccessToken))
				return;

			var botModel = new BlueSkyModel
			{
				AccessToken = bot.AccessToken!,
				RefreshToken = bot.RefreshToken,
				Handle = bot.Handle,
				PrivateKeyJson = bot.PrivateKeyJson!,
				TokenExpiresAt = bot.TokenExpiresAt,
				Did = bot.Did!,
				PdsUrl = bot.PdsUrl!
			};

			try
			{
				// 4. ВАЖНО: Проверяем и обновляем токен перед запросом к API
				await _bskyService.GetValidTokenAsync(botModel);
				if (bot.AccessToken != botModel.AccessToken)
				{
					bot.AccessToken = botModel.AccessToken;
					bot.RefreshToken = botModel.RefreshToken;
					bot.TokenExpiresAt = botModel.TokenExpiresAt;
					await _db.SaveChangesAsync();
				}

				int replyMode = bot.CommentReplyMode > 0 ? bot.CommentReplyMode : 2;
				string? replyText = null;

				await _console.Log($"Подготовка ответа на комментарий @{msg.AuthorHandle} (Режим: {replyMode})", bot.UserId, bot.Id);

				// === 1. ТОЛЬКО ШАБЛОНЫ (Spintax) ===
				if (replyMode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(bot.CommentTemplates);
				}
				// === 2. ТОЛЬКО ИИ ===
				else if (replyMode == 1)
				{
					var prompt = $"{bot.CommentPrompt}\n\nUser @{msg.AuthorHandle} left a comment on BlueSky: '{msg.Text}'. Reply briefly.";
					replyText = await _aiService.GeminiRequest(prompt, null);
				}
				// === 3. КОМБИНИРОВАННЫЙ ===
				else if (replyMode == 3)
				{
					try
					{
						var prompt = $"{bot.CommentPrompt}\n\nUser @{msg.AuthorHandle} left a comment on BlueSky: '{msg.Text}'. Reply briefly.";
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

				// =========================================================================
				// 5. ОЧЕЛОВЕЧИВАНИЕ: ИМИТАЦИЯ ЧТЕНИЯ И НАБОРА ТЕКСТА (ПАУЗА 20-35 СЕКУНД)
				// =========================================================================
				int humanPauseSeconds = Random.Shared.Next(20, 36);
				_logger.LogInformation("[BlueSky] ⏳ Пауза {Sec}с перед отправкой комментария для @{User}...", humanPauseSeconds, msg.AuthorHandle);
				await Task.Delay(TimeSpan.FromSeconds(humanPauseSeconds));

				// 6. Отправляем ответ в ветку
				bool success = await _bskyService.ReplyToThreadCommentAsync(
					replyText,
					msg.CommentUri,
					msg.CommentCid,
					msg.RootUri,
					msg.RootCid,
					botModel);

				if (success)
				{
					await _console.Log($"✅ Ответили на комментарий @{msg.AuthorHandle}: «{replyText}»", bot.UserId, bot.Id);
				}
				else
				{
					throw new Exception($"[BlueSky] Ошибка публикации ответа на комментарий {msg.CommentCid}. Включается Retry.");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка ответа на комментарий @{User}", msg.AuthorHandle);
				throw; // Чтобы сработал MassTransit Retry
			}
		}
	}
}