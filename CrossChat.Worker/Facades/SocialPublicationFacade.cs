using CrossChat.Data.Emuns;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces.Google;
using CrossChat.Integrations.Models.Site; // Подключаем SiteSettings
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Facades
{
	public class SocialPublicationFacade
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly ILogger<SocialPublicationFacade> _logger;
		private readonly IGoogleDriveUploader _googleDriveUploader;
		private readonly SiteSettings _siteSettings; // <-- ВНЕДРЯЕМ НАСТРОЙКИ ПАПКИ

		public SocialPublicationFacade(
			IServiceProvider serviceProvider,
			ILogger<SocialPublicationFacade> logger,
			IGoogleDriveUploader googleDriveUploader,
			SiteSettings siteSettings)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_googleDriveUploader = googleDriveUploader;
			_siteSettings = siteSettings;
		}

		public async Task PublishToSocialNetworkAsync(NetworkStateEntity state)
		{
			var network = (NetworkType)state.NetworkType;
			bool isTelegram = network == NetworkType.TelegramChannel || network == NetworkType.TelegramPublic;

			var mediaList = state.Post.Media
				.Where(m => isTelegram || m.MediaType != MediaType.Audio)
				.OrderBy(m => m.SortOrder)
				.ToList();

			var mediaPayloads = new List<string>();
			var tempFilesToCleanup = new List<string>();

			// Гарантируем, что проверенная папка temp существует
			string tempFolder = _siteSettings.TempFolder;
			if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

			try
			{
				foreach (var media in mediaList)
				{
					try
					{
						// ДЛЯ ВИДЕО, АУДИО И ТЯЖЕЛЫХ ФАЙЛОВ (> 3 МБ):
						// Сохраняем потоком в нашу проверенную папку TempFolder!
						// Расход RAM = 80 Килобайт! Никаких OutOfMemory!
						if (media.MediaType == MediaType.Video || media.MediaType == MediaType.Audio || media.FileSizeBytes > 3 * 1024 * 1024)
						{
							// Определяем расширение без дублирования:
							string ext = Path.GetExtension(media.FileName);
							if (string.IsNullOrEmpty(ext))
							{
								ext = media.MediaType == MediaType.Video ? ".mp4"
									: media.MediaType == MediaType.Audio ? ".ogg"
									: ".jpg";
							}

							// ЧИСТОЕ ИМЯ: e5629f7b-8ec0-4401-8837-86d7772e4eba.mp4
							string tempFileName = $"{Guid.NewGuid()}{ext.ToLowerInvariant()}";
							string localPath = Path.Combine(tempFolder, tempFileName);
							tempFilesToCleanup.Add(localPath);

							using (var driveStream = await _googleDriveUploader.GetFileStreamAsync(media.GoogleDriveFileId))
							using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
							{
								await driveStream.CopyToAsync(fs);
							}

							mediaPayloads.Add(localPath);
						}
						else
						{
							// Для легких фото (200-500 КБ) оставляем Base64
							using var stream = await _googleDriveUploader.GetFileStreamAsync(media.GoogleDriveFileId);
							using var ms = new MemoryStream();
							await stream.CopyToAsync(ms);
							var base64 = Convert.ToBase64String(ms.ToArray());
							mediaPayloads.Add(base64);
						}
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Ошибка при получении медиафайла {FileId} из Google Drive", media.GoogleDriveFileId);
						throw;
					}
				}

				var publisher = _serviceProvider.GetKeyedService<ISocialPublisher>(network);
				if (publisher == null)
				{
					throw new NotImplementedException($"Публикация в соцсеть '{network}' не зарегистрирована в Keyed Services.");
				}

				await publisher.PublishAsync(state, state.Caption, mediaPayloads);
			}
			finally
			{
				// Очищаем временные файлы
				foreach (var path in tempFilesToCleanup)
				{
					try { if (File.Exists(path)) File.Delete(path); } catch { }
				}
			}
		}
	}
}