using CrossChat.Data.Emuns;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces.Google;
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

		public SocialPublicationFacade(
			IServiceProvider serviceProvider,
			ILogger<SocialPublicationFacade> logger,
			IGoogleDriveUploader googleDriveUploader)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
			_googleDriveUploader = googleDriveUploader;
		}

		public async Task PublishToSocialNetworkAsync(NetworkStateEntity state)
		{
			var network = (NetworkType)state.NetworkType;
			var mediaList = state.Post.Media
				.Where(m => m.MediaType == MediaType.Image) // TODO заглушка на ремя разработи!!!!!!
				.OrderBy(m => m.SortOrder)
				.ToList();
			var mediaPayloads = new List<string>();

			// Скачиваем каждый файл из Google Drive для публикации
			foreach (var media in mediaList)
			{
				try
				{
					using var stream = await _googleDriveUploader.GetFileStreamAsync(media.GoogleDriveFileId);
					using var ms = new MemoryStream();
					await stream.CopyToAsync(ms);
					var base64 = Convert.ToBase64String(ms.ToArray());

					// Если видео — добавляем data-url префикс, чтобы InstagramService определил его как mp4
					if (media.MediaType == MediaType.Video)
					{
						var mime = string.IsNullOrEmpty(media.MimeType) ? "video/mp4" : media.MimeType;
						mediaPayloads.Add($"data:{mime};base64,{base64}");
					}
					else
					{
						mediaPayloads.Add(base64);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка при получении медиафайла {FileId} из Google Drive", media.GoogleDriveFileId);
					throw;
				}
			}

			// ДОСТАЕМ СЕРВИС НАПРЯМУЮ ПО КЛЮЧУ ENUM (Keyed Service)
			var publisher = _serviceProvider.GetKeyedService<ISocialPublisher>(network);

			if (publisher == null)
			{
				throw new NotImplementedException($"Публикация в соцсеть '{network}' не зарегистрирована в Keyed Services.");
			}

			// Вызываем публикацию
			await publisher.PublishAsync(state, state.Caption, mediaPayloads);
		}
	}
}