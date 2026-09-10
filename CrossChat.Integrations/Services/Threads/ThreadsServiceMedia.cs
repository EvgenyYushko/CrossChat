using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Services
{
	public partial class ThreadsService
	{
		/// <summary>
		/// ГЛАВНЫЙ МЕТОД: Публикация поста в Threads (Текст, 1 фото/видео или смешанная Карусель до 10 медиа)
		/// </summary>
		public async Task<bool> CreatePostAsync(string caption, List<string> imagesBase64, string accessToken)
		{
			// Запускаем фоновую чистку старого мусора
			CleanupOldTempFiles();

			var tempFilesTracker = new List<string>();
			try
			{
				string creationId;

				// 1. СЦЕНАРИЙ: ТЕКСТОВЫЙ ПОСТ (без медиа)
				if (imagesBase64 == null || !imagesBase64.Any())
				{
					var textUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";
					var textPayload = new
					{
						media_type = "TEXT",
						text = caption
					};

					var textResp = await _httpClient.PostAsJsonAsync(textUrl, textPayload);
					if (!textResp.IsSuccessStatusCode)
					{
						var error = await textResp.Content.ReadAsStringAsync();
						_logger.LogError($"[Threads] Ошибка создания текстового контейнера: {error}");
						return false;
					}

					var textJson = await textResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = textJson.GetProperty("id").GetString()!;
				}
				// 2. СЦЕНАРИЙ: ОДИНОЧНОЕ МЕДИА (1 фото ИЛИ 1 видео)
				else if (imagesBase64.Count == 1)
				{
					var (mediaUrl, localPath) = await SaveMediaLocallyAsync(imagesBase64.First());
					tempFilesTracker.Add(localPath);

					bool isVideo = mediaUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

					var singleMediaUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";

					// Формируем payload в зависимости от типа медиа: IMAGE или VIDEO
					object mediaPayload = isVideo
						? new
						{
							media_type = "VIDEO",
							video_url = mediaUrl,
							text = caption
						}
						: new
						{
							media_type = "IMAGE",
							image_url = mediaUrl,
							text = caption
						};

					// Пауза 500 мс для фиксации файла веб-сервером
					await Task.Delay(500);

					var mediaResp = await _httpClient.PostAsJsonAsync(singleMediaUrl, mediaPayload);
					if (!mediaResp.IsSuccessStatusCode)
					{
						var error = await mediaResp.Content.ReadAsStringAsync();
						_logger.LogError($"[Threads] Ошибка создания медиа-контейнера: {error}");
						return false;
					}

					var mediaJson = await mediaResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = mediaJson.GetProperty("id").GetString()!;

					// Ожидаем обработки медиа сервером Threads (для видео может занять до 30-60 сек)
					bool isReady = await WaitForMediaReadyAsync(creationId, accessToken);
					if (!isReady) return false;
				}
				// 3. СЦЕНАРИЙ: КАРУСЕЛЬ (до 10 фото и/или видео)
				else
				{
					var childrenIds = new List<string>();

					// А. Создаем отдельный контейнер для каждого слайда (фото или видео)
					foreach (var base64Item in imagesBase64.Take(10))
					{
						var (mediaUrl, localPath) = await SaveMediaLocallyAsync(base64Item);
						tempFilesTracker.Add(localPath);

						bool isVideo = mediaUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

						var itemUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";

						object itemPayload = isVideo
							? new
							{
								media_type = "VIDEO",
								video_url = mediaUrl,
								is_carousel_item = true
							}
							: new
							{
								media_type = "IMAGE",
								image_url = mediaUrl,
								is_carousel_item = true
							};

						await Task.Delay(500);

						var itemResp = await _httpClient.PostAsJsonAsync(itemUrl, itemPayload);
						if (!itemResp.IsSuccessStatusCode)
						{
							var error = await itemResp.Content.ReadAsStringAsync();
							_logger.LogError($"[Threads] Ошибка создания элемента карусели: {error}");
							return false;
						}

						var itemJson = await itemResp.Content.ReadFromJsonAsync<JsonElement>();
						string itemId = itemJson.GetProperty("id").GetString()!;
						childrenIds.Add(itemId);
					}

					// Б. Ждем готовности всех дочерних слайдов
					foreach (var childId in childrenIds)
					{
						bool isChildReady = await WaitForMediaReadyAsync(childId, accessToken);
						if (!isChildReady) return false;
					}

					// В. Создаем родительский контейнер карусели
					var carouselUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";
					var carouselPayload = new
					{
						media_type = "CAROUSEL",
						children = childrenIds,
						text = caption
					};

					var carouselResp = await _httpClient.PostAsJsonAsync(carouselUrl, carouselPayload);
					if (!carouselResp.IsSuccessStatusCode)
					{
						var error = await carouselResp.Content.ReadAsStringAsync();
						_logger.LogError($"[Threads] Ошибка создания родительской карусели: {error}");
						return false;
					}

					var carouselJson = await carouselResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = carouselJson.GetProperty("id").GetString()!;

					// Ждем готовности родительской карусели
					bool isCarouselReady = await WaitForMediaReadyAsync(creationId, accessToken);
					if (!isCarouselReady) return false;
				}

				// 4. ФИНАЛЬНАЯ ПУБЛИКАЦИЯ
				var publishUrl = $"https://graph.threads.net/v1.0/me/threads_publish?creation_id={creationId}&access_token={accessToken}";
				var publishResp = await _httpClient.PostAsync(publishUrl, null);

				if (publishResp.IsSuccessStatusCode)
				{
					_logger.LogInformation($"[Threads] ✅ Пост успешно опубликован в Threads (ID контейнера: {creationId})");
					return true;
				}

				var publishError = await publishResp.Content.ReadAsStringAsync();
				_logger.LogError($"[Threads] ❌ Ошибка финальной публикации в Threads: {publishError}");
				return false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Threads] Критическая ошибка при публикации поста");
				return false;
			}
			finally
			{
				// Удаляем временные файлы с сервера
				foreach (var localPath in tempFilesTracker)
				{
					try
					{
						if (File.Exists(localPath))
						{
							File.Delete(localPath);
							_logger.LogInformation($"Удален временный файл: {localPath}");
						}
					}
					catch (Exception ex)
					{
						_logger.LogError($"Не удалось удалить файл {localPath}: {ex.Message}");
					}
				}
			}
		}

		private async Task<(string PublicUrl, string LocalPath)> SaveMediaLocallyAsync(string base64String)
		{
			string tempFolder = _siteSettings.TempFolder;

			if (!Directory.Exists(tempFolder))
			{
				Directory.CreateDirectory(tempFolder);
			}

			string extension = ".jpg";
			string cleanBase64 = base64String;

			if (base64String.Contains(","))
			{
				var parts = base64String.Split(',');
				var metaInfo = parts[0].ToLower();
				cleanBase64 = parts[1];

				if (metaInfo.Contains("video/mp4") || metaInfo.Contains("video/")) extension = ".mp4";
				else if (metaInfo.Contains("image/png")) extension = ".png";
			}

			string fileName = $"{Guid.NewGuid()}{extension}";
			string localPath = Path.Combine(tempFolder, fileName);

			byte[] fileBytes = Convert.FromBase64String(cleanBase64);
			await File.WriteAllBytesAsync(localPath, fileBytes);

			// Очищаем C2PA метку ИИ из видео через FFmpeg
			if (extension == ".mp4")
			{
				await VideoService.StripAiMetadataAsync(localPath, _logger);
			}

			string publicUrl = $"{_siteSettings.AppUrl.TrimEnd('/')}/temp_media/{fileName}";
			return (publicUrl, localPath);
		}

		/// <summary>
		/// Очистка старых файлов, которые могли остаться при экстренном падении сервера
		/// </summary>
		private void CleanupOldTempFiles()
		{
			try
			{
				string tempFolder = _siteSettings.TempFolder;

				if (Directory.Exists(tempFolder))
				{
					var oldFiles = Directory.GetFiles(tempFolder)
						.Select(f => new FileInfo(f))
						.Where(f => f.CreationTime < DateTimeNow.AddMinutes(-15)) // Удаляем все, что старше 15 минут
						.ToList();

					foreach (var file in oldFiles)
					{
						file.Delete();
						_logger.LogInformation($"[Очистка] Удален старый временный файл: {file.Name}");
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError($"Ошибка при очистке старых файлов: {ex.Message}");
			}
		}
	}
}
