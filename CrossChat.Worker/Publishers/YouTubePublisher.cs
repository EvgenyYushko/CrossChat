using System.Text.RegularExpressions;
using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Publishers
{
	public class YouTubePublisher : ISocialPublisher
	{
		public NetworkType Network => NetworkType.YouTube;

		private readonly AppDbContext _db;
		private readonly IYouTubeService _youTubeService;
		private readonly ILogger<YouTubePublisher> _logger;

		public YouTubePublisher(
			AppDbContext db,
			IYouTubeService youTubeService,
			ILogger<YouTubePublisher> logger)
		{
			_db = db;
			_youTubeService = youTubeService;
			_logger = logger;
		}

		public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
		{
			// 1. Ищем канал в БД
			var channel = await _db.YouTubeSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
			if (channel == null)
			{
				throw new Exception($"Не найдены настройки YouTube-канала (BotId: {state.BotId})");
			}

			_logger.LogInformation("Начало отправки публикации на YouTube канал '{Title}' (@{Handle}).",
				channel.ChannelTitle, channel.CustomUrl);

			// 2. Ищем видео и возможную обложку (картинку)
			bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
			bool isImage(string s) => !isVideo(s) && !s.Contains("audio/");

			var videoItem = images?.FirstOrDefault(isVideo);
			var coverImageItem = images?.FirstOrDefault(isImage); // Картинка для кастомного постера

			if (string.IsNullOrEmpty(videoItem))
			{
				throw new InvalidOperationException("Для публикации в YouTube требуется видеофайл. Текстовые посты и одиночные фото не поддерживаются.");
			}

			// 3. Обновляем токен доступа при необходимости
			if (!channel.TokenExpiresAt.HasValue || channel.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(5))
			{
				if (!string.IsNullOrEmpty(channel.RefreshToken))
				{
					_logger.LogInformation("[YouTube] Токен для '{Title}' истекает. Обновляем...", channel.ChannelTitle);
					var refreshResult = await _youTubeService.RefreshAccessTokenAsync(channel.RefreshToken);

					if (refreshResult != null && !string.IsNullOrEmpty(refreshResult.Value.AccessToken))
					{
						channel.AccessToken = refreshResult.Value.AccessToken;
						channel.TokenExpiresAt = DateTimeNow.AddSeconds(refreshResult.Value.ExpiresIn);
						await _db.SaveChangesAsync();
						_logger.LogInformation("[YouTube] Токен успешно обновлен.");
					}
					else
					{
						throw new Exception($"Не удалось обновить токен доступа для YouTube канала '{channel.ChannelTitle}'.");
					}
				}
			}

			// 4. Достаем бинарные данные видео и очищаем метаданные
			string cleanBase64 = videoItem.Contains(",") ? videoItem.Split(',')[1] : videoItem;
			byte[] videoBytes = Convert.FromBase64String(cleanBase64);

			_logger.LogInformation("[YouTube] Очистка видео от метаданных ИИ перед публикацией...");
			videoBytes = await VideoService.StripAiMetadataAsync(videoBytes, _logger);

			// 5. Заголовок, описание и теги
			string fullCaption = caption ?? string.Empty;
			string title = "Новое видео";
			string description = fullCaption;

			if (!string.IsNullOrWhiteSpace(fullCaption))
			{
				var lines = fullCaption.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
				if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
				{
					title = lines[0].Trim();
					if (title.Length > 90) title = title.Substring(0, 87) + "...";
				}
			}

			if (!description.Contains("#shorts", StringComparison.OrdinalIgnoreCase) &&
				!title.Contains("#shorts", StringComparison.OrdinalIgnoreCase))
			{
				description = (description + "\n\n#Shorts").Trim();
			}

			var tags = new List<string>();
			var matches = Regex.Matches(description, @"#(\w+)");
			foreach (Match m in matches)
			{
				var tag = m.Groups[1].Value;
				if (!tags.Contains(tag)) tags.Add(tag);
			}

			string privacy = string.IsNullOrWhiteSpace(state.PrivacyStatus) ? "public" : state.PrivacyStatus;

			_logger.LogInformation("[YouTube] Загрузка видео ({Bytes} байт), заголовок: «{Title}», приватность: {P}...", 
				videoBytes.Length, title, privacy);

			// 6. Загружаем видео через YouTube Data API v3
			var uploadResult = await _youTubeService.UploadVideoAsync(
				videoBytes,
				title,
				description,
				tags,
				privacy,
				channel.AccessToken!);

			if (!uploadResult.Success || string.IsNullOrEmpty(uploadResult.VideoId))
			{
				throw new Exception($"Ошибка при публикации видео на YouTube: {uploadResult.ErrorMessage}");
			}

			string videoId = uploadResult.VideoId;
			_logger.LogInformation("🎉 Видео успешно опубликовано! VideoId: {Id}", videoId);

			// === 7. ФИЧА: УСТАНОВКА КАСТОМНОЙ ОБЛОЖКИ (THUMBNAIL) ===
			if (!string.IsNullOrEmpty(coverImageItem))
			{
				try
				{
					_logger.LogInformation("[YouTube] Найдено изображение обложки. Загрузка постера для видео {VideoId}...", videoId);
					string imgBase64 = coverImageItem.Contains(",") ? coverImageItem.Split(',')[1] : coverImageItem;
					byte[] coverBytes = Convert.FromBase64String(imgBase64);

					await _youTubeService.SetThumbnailAsync(videoId, coverBytes, channel.AccessToken!);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось установить кастомную обложку для {VideoId}. Видео осталось со стандартным превью.", videoId);
				}
			}

			// === 8. ФИЧА: ПУБЛИКАЦИЯ ПЕРВОГО ЗАКРЕПЛЕННОГО КОММЕНТАРИЯ ===
			if (!string.IsNullOrWhiteSpace(state.FirstComment))
			{
				try
				{
					_logger.LogInformation("[YouTube] Отправка первого комментария под видео {VideoId}...", videoId);
					await _youTubeService.AddCommentAsync(videoId, state.FirstComment, channel.AccessToken!);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось опубликовать первый комментарий под видео {VideoId}", videoId);
				}
			}
		}
	}
}