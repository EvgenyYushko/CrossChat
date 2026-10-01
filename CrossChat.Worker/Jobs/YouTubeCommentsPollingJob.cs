using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Jobs;

[DisallowConcurrentExecution]
public class YouTubeCommentsPollingJob : IJob
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<YouTubeCommentsPollingJob> _logger;
	private readonly IHostEnvironment _env;

	// Смещение для Минска/Москвы (+3 часа), чтобы в логах Render видеть привычное время
	private static readonly TimeSpan BelarusOffset = TimeSpan.FromHours(3);

	public YouTubeCommentsPollingJob(IServiceScopeFactory scopeFactory
		, ILogger<YouTubeCommentsPollingJob> logger
		, IHostEnvironment env
		)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
		_env = env;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		if (_env.IsDevelopment())
		{
			return;
		}

		using var scope = _scopeFactory.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
		var ytService = scope.ServiceProvider.GetRequiredService<IYouTubeService>();
		var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

		// Текущее время сервера строго в UTC
		var nowUtc = DateTime.UtcNow;
		var nowBelarus = nowUtc + BelarusOffset;

		// 1. Ищем каналы, где включены автоответы
		var channels = await db.YouTubeSettings
			.Where(y => y.IsActive && y.IsCommentsEnabled && !string.IsNullOrEmpty(y.AccessToken))
			.ToListAsync();

		if (!channels.Any()) return;

		_logger.LogInformation("🕒 [YouTube Poller] Старт опроса. Сервер Render (UTC): {NowUtc:yyyy-MM-dd HH:mm:ss} | Время Минск: {NowBy:HH:mm:ss}",
			nowUtc, nowBelarus);

		foreach (var channel in channels)
		{
			try
			{
				_logger.LogInformation("==================================================================");
				_logger.LogInformation("🔍 [YouTube Poller] Канал «{Title}» (ChannelId: {ChannelId})", channel.ChannelTitle, channel.ChannelId);

				// 2. Проверка и авто-рефреш токена
				if (!channel.TokenExpiresAt.HasValue || channel.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(5))
				{
					if (!string.IsNullOrEmpty(channel.RefreshToken))
					{
						_logger.LogInformation("🔑 [YouTube Poller] Токен истекает. Обновляем через Google OAuth...");
						var refreshed = await ytService.RefreshAccessTokenAsync(channel.RefreshToken);
						if (refreshed != null && !string.IsNullOrEmpty(refreshed.Value.AccessToken))
						{
							channel.AccessToken = refreshed.Value.AccessToken;
							channel.TokenExpiresAt = DateTimeNow.AddSeconds(refreshed.Value.ExpiresIn);
							await db.SaveChangesAsync();
							_logger.LogInformation("✅ [YouTube Poller] Токен успешно обновлен.");
						}
					}
				}

				// 3. Вычисляем временной порог (Threshold)
				// Если опрашиваем впервые — берем комментарии за последние 2 часа, иначе — с момента предыдущего опроса
				DateTime thresholdUtc = channel.LastCommentProcessedAt ?? nowUtc.AddHours(-2);
				DateTime thresholdBelarus = thresholdUtc + BelarusOffset;

				_logger.LogInformation("⏱ [YouTube Poller] Порог фильтра: всё что НОВЕЕ {ThUtc:yyyy-MM-dd HH:mm:ss} UTC ({ThBy:HH:mm:ss} по Минску)",
					thresholdUtc, thresholdBelarus);

				// 4. Запрос к YouTube API (последние 20 комментариев под всеми роликами канала)
				var comments = await ytService.GetRecentCommentsAsync(channel.ChannelId, channel.AccessToken!);

				_logger.LogInformation("📥 [YouTube Poller] YouTube API вернул {Count} комментариев.", comments.Count);

				int acceptedCount = 0;
				int skippedOldCount = 0;
				int skippedSelfCount = 0;

				DateTime? maxCommentDateUtc = null;

				foreach (var c in comments)
				{
					// Гарантируем, что дата строго в UTC
					var commentDateUtc = c.PublishedAt.Kind == DateTimeKind.Utc
						? c.PublishedAt
						: c.PublishedAt.ToUniversalTime();

					var commentDateBelarus = commentDateUtc + BelarusOffset;

					// ФИЛЬТР 1: ЭХО-ЗАЩИТА (свой собственный комментарий)
					if (!string.IsNullOrEmpty(c.AuthorChannelId) && c.AuthorChannelId == channel.ChannelId)
					{
						skippedSelfCount++;
						_logger.LogInformation("   ⏭ [СВОЙ КОММЕНТАРИЙ - ПРОПУСК] Автор: {Author} | Текст: «{Text}» | Дата: {DateBy:HH:mm:ss} Минск",
							c.AuthorDisplayName, c.Text, commentDateBelarus);
						continue;
					}

					// ФИЛЬТР 2: ПРОВЕРКА ПО ВРЕМЕНИ (Старый / Новый)
					if (commentDateUtc <= thresholdUtc)
					{
						skippedOldCount++;
						var delayMinutes = (nowUtc - commentDateUtc).TotalMinutes;
						_logger.LogInformation("   ⏳ [СТАРЫЙ - ПРОПУСК] От: @{User} ({Ago:F0} мин назад) | Опубликован: {DateUtc:yyyy-MM-dd HH:mm:ss} UTC ({DateBy:HH:mm:ss} Минск) <= Порог {ThBy:HH:mm:ss} | Текст: «{Text}»",
							c.AuthorDisplayName, delayMinutes, commentDateUtc, commentDateBelarus, thresholdBelarus, c.Text);
						continue;
					}

					// ЕСЛИ ДОШЛИ СЮДА — КОММЕНТАРИЙ СВЕЖИЙ И ПОДЛЕЖИТ ОТВЕТУ!
					acceptedCount++;
					_logger.LogInformation("   🔥 [ПРИНЯТ В ОБРАБОТКУ] От: @{User} | Видео: {Vid} | Опубликован: {DateBy:HH:mm:ss} Минск | Текст: «{Text}»",
						c.AuthorDisplayName, c.VideoId, commentDateBelarus, c.Text);

					// Запоминаем максимальную дату среди обработанных комментариев
					if (!maxCommentDateUtc.HasValue || commentDateUtc > maxCommentDateUtc.Value)
					{
						maxCommentDateUtc = commentDateUtc;
					}

					// Публикуем событие в шину MassTransit
					await publishEndpoint.Publish(new YouTubeCommentReceived
					{
						BotDbId = channel.Id,
						ChannelId = channel.ChannelId,
						VideoId = c.VideoId,
						CommentId = c.CommentId,
						Text = c.Text,
						Username = c.AuthorDisplayName,
						AuthorChannelId = c.AuthorChannelId
					});
				}

				_logger.LogInformation("📊 [YouTube Poller Итог канала «{Title}»]: Принято: {Acc}, Пропущено старых: {Old}, Пропущено своих: {Self}",
					channel.ChannelTitle, acceptedCount, skippedOldCount, skippedSelfCount);

				// 5. Обновляем метку последнего опроса в базе данных
				// Если были новые комментарии — сдвигаем порог на дату самого свежего из них
				// Если новых не было — ставим текущий nowUtc
				channel.LastCommentProcessedAt = maxCommentDateUtc ?? nowUtc;
				await db.SaveChangesAsync();

				_logger.LogInformation("💾 [YouTube Poller] Новый сохраненный порог канала: {NextTh:yyyy-MM-dd HH:mm:ss} UTC", channel.LastCommentProcessedAt);
				_logger.LogInformation("==================================================================");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [YouTube Poller] Ошибка при обработке канала {Title}", channel.ChannelTitle);
			}
		}
	}
}