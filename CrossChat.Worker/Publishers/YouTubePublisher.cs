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
			// 1. Ищем подключенный канал в БД
			var channel = await _db.YouTubeSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
			if (channel == null)
			{
				throw new Exception($"Не найдены настройки YouTube-канала (BotId: {state.BotId})");
			}

			_logger.LogInformation("Начало отправки публикации на YouTube канал '{Title}' (@{Handle}).",
				channel.ChannelTitle, channel.CustomUrl);

			// 2. Ищем видео среди медиафайлов
			bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
			var videoItem = images?.FirstOrDefault(isVideo);

			if (string.IsNullOrEmpty(videoItem))
			{
				throw new InvalidOperationException("Для публикации в YouTube требуется видеофайл. Текстовые посты и одиночные фото не поддерживаются.");
			}

			// 3. Проверяем и обновляем токен доступа, если он истекает в ближайшие 5 минут
			if (!channel.TokenExpiresAt.HasValue || channel.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(5))
			{
				if (!string.IsNullOrEmpty(channel.RefreshToken))
				{
					_logger.LogInformation("[YouTube] Токен для '{Title}' истекает. Обновляем перед публикацией...", channel.ChannelTitle);
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

			// 4. Достаем бинарные данные видео из Base64
			string cleanBase64 = videoItem.Contains(",") ? videoItem.Split(',')[1] : videoItem;
			byte[] videoBytes = Convert.FromBase64String(cleanBase64);

			// === 4.1. ВАЖНО: УДАЛЯЕМ МЕТКУ ИИ (C2PA) ЧЕРЕЗ FFMPEG ===
			_logger.LogInformation("[YouTube] Очистка видео от метаданных ИИ (C2PA) перед публикацией...");
			videoBytes = await VideoService.StripAiMetadataAsync(videoBytes, _logger);

			// 5. Формируем Заголовок, Описание и Теги
			string fullCaption = caption ?? string.Empty;
			string title = "Новое видео";
			string description = fullCaption;

			if (!string.IsNullOrWhiteSpace(fullCaption))
			{
				// Берем первую строку текста в качестве заголовка (до 90 символов)
				var lines = fullCaption.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
				if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
				{
					title = lines[0].Trim();
					if (title.Length > 90) title = title.Substring(0, 87) + "...";
				}
			}

			// Автоматически добавляем тег #Shorts, если его нет
			if (!description.Contains("#shorts", StringComparison.OrdinalIgnoreCase) &&
				!title.Contains("#shorts", StringComparison.OrdinalIgnoreCase))
			{
				description = (description + "\n\n#Shorts").Trim();
			}

			// Извлекаем хештеги в официальные теги видеоролика
			var tags = new List<string>();
			var matches = Regex.Matches(description, @"#(\w+)");
			foreach (Match m in matches)
			{
				var tag = m.Groups[1].Value;
				if (!tags.Contains(tag)) tags.Add(tag);
			}

			_logger.LogInformation("[YouTube] Загрузка видео ({Bytes} байт) с заголовком: «{Title}»...", videoBytes.Length, title);

			// 6. Вызываем загрузку через YouTube Data API v3
			var uploadResult = await _youTubeService.UploadVideoAsync(
				videoBytes,
				title,
				description,
				tags,
				channel.AccessToken!);

			if (!uploadResult.Success)
			{
				throw new Exception($"Ошибка при публикации видео на YouTube: {uploadResult.ErrorMessage}");
			}

			_logger.LogInformation("🎉 Видео успешно опубликовано на YouTube! Ссылка: https://youtube.com/shorts/{VideoId}", uploadResult.VideoId);
		}
	}
}