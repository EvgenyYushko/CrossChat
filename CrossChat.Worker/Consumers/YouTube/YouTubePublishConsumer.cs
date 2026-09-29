using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Consumers.YouTube;

public class YouTubePublishConsumer : IConsumer<PublishYouTubeReplyCommand>
{
	private readonly IYouTubeService _youTubeService;
	private readonly AppDbContext _db;
	private readonly ILogger<YouTubePublishConsumer> _logger;

	public YouTubePublishConsumer(
		IYouTubeService youTubeService,
		AppDbContext db,
		ILogger<YouTubePublishConsumer> logger)
	{
		_youTubeService = youTubeService;
		_db = db;
		_logger = logger;
	}

	public async Task Consume(ConsumeContext<PublishYouTubeReplyCommand> context)
	{
		var msg = context.Message;
		var settings = await _db.YouTubeSettings.FirstOrDefaultAsync(s => s.Id == msg.BotDbId);
		if (settings == null || string.IsNullOrEmpty(settings.AccessToken)) return;

		// Обновляем токен при необходимости
		if (!settings.TokenExpiresAt.HasValue || settings.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(5))
		{
			if (!string.IsNullOrEmpty(settings.RefreshToken))
			{
				var refresh = await _youTubeService.RefreshAccessTokenAsync(settings.RefreshToken);
				if (refresh != null && !string.IsNullOrEmpty(refresh.Value.AccessToken))
				{
					settings.AccessToken = refresh.Value.AccessToken;
					settings.TokenExpiresAt = DateTimeNow.AddSeconds(refresh.Value.ExpiresIn);
					await _db.SaveChangesAsync();
				}
			}
		}

		_logger.LogInformation("🚀 [YouTube Publish] Отправка ответа для @{User} на комментарий {CommentId}...", msg.Username, msg.CommentId);

		bool success = await _youTubeService.ReplyToCommentAsync(msg.CommentId, msg.ReplyText, settings.AccessToken);
		if (success)
		{
			_logger.LogInformation("✅ [YouTube Publish] Ответ успешно опубликован под комментарием {CommentId}!", msg.CommentId);
		}
	}
}