using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.YouTube;

public class YouTubeReplyConsumer : IConsumer<YouTubeCommentReceived>
{
	private readonly ILogger<YouTubeReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IAiService _aiService;
	private readonly IDatabase _redis;

	public YouTubeReplyConsumer(
		ILogger<YouTubeReplyConsumer> logger,
		AppDbContext db,
		IAiService aiService,
		IConnectionMultiplexer redis)
	{
		_logger = logger;
		_db = db;
		_aiService = aiService;
		_redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<YouTubeCommentReceived> context)
	{
		var msg = context.Message;

		// 1. ЗАЩИТА ОТ ДУБЛЕЙ В РЕДИС
		var lockKey = $"processed_yt_comment:{msg.CommentId}";
		if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromHours(48), When.NotExists))
		{
			_logger.LogInformation("[YouTube] Комментарий {CommentId} уже в обработке. Пропускаем.", msg.CommentId);
			return;
		}

		// 2. НЕ БОЛЕЕ 1 ОТВЕТА ОДНОМУ ЮЗЕРУ ПОД ОДНИМ ВИДЕО В ТЕЧЕНИЕ 24 ЧАСОВ
		if (!string.IsNullOrEmpty(msg.AuthorChannelId) && !string.IsNullOrEmpty(msg.VideoId))
		{
			var userVideoKey = $"yt_answered_user:{msg.VideoId}:{msg.AuthorChannelId}";
			if (!await _redis.StringSetAsync(userVideoKey, "answered", TimeSpan.FromHours(24), When.NotExists))
			{
				_logger.LogInformation("[YouTube] Мы уже отвечали пользователю {User} под видео {VideoId}. Пропускаем.", msg.Username, msg.VideoId);
				return;
			}
		}

		// 3. Подгружаем настройки канала
		var settings = await _db.YouTubeSettings.FirstOrDefaultAsync(s => s.Id == msg.BotDbId);
		if (settings == null || !settings.IsActive || !settings.IsCommentsEnabled || string.IsNullOrEmpty(settings.AccessToken))
			return;

		int replyMode = settings.CommentReplyMode > 0 ? settings.CommentReplyMode : 2;
		string? replyText = null;

		try
		{
			_logger.LogInformation("[YouTube] Обработка коммента от @{User}: «{Text}» (Режим: {Mode})", msg.Username, msg.Text, replyMode);

			// === ГЕНЕРАЦИЯ ТЕКСТА (Шаблоны со Spintax / ИИ) ===
			if (replyMode == 2)
			{
				replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
			}
			else if (replyMode == 1)
			{
				replyText = await GenerateAiYouTubeReply(settings, msg);
			}
			else if (replyMode == 3)
			{
				try
				{
					replyText = await GenerateAiYouTubeReply(settings, msg);
				}
				catch (Exception aiEx)
				{
					_logger.LogWarning(aiEx, "[YouTube] Сбой ИИ для комментария {CommentId}. Переход на резервный шаблон.", msg.CommentId);
				}

				if (string.IsNullOrWhiteSpace(replyText))
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(settings.CommentTemplates);
				}
			}

			if (string.IsNullOrWhiteSpace(replyText))
			{
				_logger.LogWarning("[YouTube] Текст ответа пуст для комментария {CommentId}", msg.CommentId);
				return;
			}

			// ==================================================================================
			// 4. УМНАЯ СКОЛЬЗЯЩАЯ ОЧЕРЕДЬ (PACING QUEUE): ИСКЛЮЧАЕТ СПАМ-БАН И ОДНОВРЕМЕННЫЕ ВЫСТРЕЛЫ
			// ==================================================================================
			var queueTimeKey = $"yt:next_reply_time:{settings.Id}";
			var now = DateTime.UtcNow;

			var lastScheduledTicks = await _redis.StringGetAsync(queueTimeKey);
			DateTime baseTime = now;

			if (lastScheduledTicks.HasValue && long.TryParse(lastScheduledTicks, out long ticks))
			{
				var lastTime = new DateTime(ticks, DateTimeKind.Utc);
				if (lastTime > now)
				{
					baseTime = lastTime;
				}
			}

			// Случайная задержка человека: от 25 до 50 секунд
			int humanPauseSeconds = Random.Shared.Next(25, 51);
			var targetTimeUtc = baseTime.AddSeconds(humanPauseSeconds);

			await _redis.StringSetAsync(queueTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromHours(2));

			var finalDelay = targetTimeUtc - now;

			_logger.LogInformation("⏱ [YouTube] Ответ для @{User} запланирован через {Delay:F0} сек. Текст: «{Reply}»",
				msg.Username, finalDelay.TotalSeconds, replyText);

			// Планируем отправку через Quartz/MassTransit!
			await context.SchedulePublish(finalDelay, new PublishYouTubeReplyCommand
			{
				BotDbId = settings.Id,
				CommentId = msg.CommentId,
				ReplyText = replyText,
				Username = msg.Username
			});
		}
		catch (Exception ex)
		{
			await _redis.KeyDeleteAsync(lockKey);
			_logger.LogError(ex, "Ошибка при подготовке ответа YouTube на комментарий {CommentId}", msg.CommentId);
		}
	}

	private async Task<string?> GenerateAiYouTubeReply(YouTubeSettings settings, YouTubeCommentReceived msg)
	{
		var prompt = $"{settings.CommentPrompt}\n\nUser @{msg.Username} wrote a comment on your YouTube video: \"{msg.Text}\". Write a friendly, engaging and concise reply in the same language.";
		return await _aiService.GeminiRequest(prompt, null);
	}
}