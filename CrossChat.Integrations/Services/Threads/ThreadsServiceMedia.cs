using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Services
{
	public partial class ThreadsService
	{
		/// <summary>
		/// ГЛАВНЫЙ МЕТОД: Публикация поста в Threads (Текст, 1 фото или Карусель до 10 фото)
		/// </summary>
		public async Task<bool> CreatePostAsync(string caption, List<string> imagesBase64, string accessToken)
		{
			// Запускаем фоновую чистку старого мусора (на случай прошлых падений)
			CleanupOldTempFiles();

			var tempFilesTracker = new List<string>();
			try
			{
				string creationId;

				// 1. СЦЕНАРИЙ: ТЕКСТОВЫЙ ПОСТ (без фото)
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
				// 2. СЦЕНАРИЙ: ПОСТ С ОДНИМ ФОТО
				else if (imagesBase64.Count == 1)
				{
					var (mediaUrl, localPath) = await SaveMediaLocallyAsync(imagesBase64.First());
					tempFilesTracker.Add(localPath); // Добавляем в трекер для последующего удаления

					var singleImageUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";
					var imagePayload = new
					{
						media_type = "IMAGE",
						image_url = mediaUrl,
						text = caption
					};

					var imageResp = await _httpClient.PostAsJsonAsync(singleImageUrl, imagePayload);
					if (!imageResp.IsSuccessStatusCode)
					{
						var error = await imageResp.Content.ReadAsStringAsync();
						_logger.LogError($"[Threads] Ошибка создания фото-контейнера: {error}");
						return false;
					}

					var imageJson = await imageResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = imageJson.GetProperty("id").GetString()!;

					// Ожидаем обработки изображения сервером Threads
					bool isReady = await WaitForMediaReadyAsync(creationId, accessToken);
					if (!isReady) return false;
				}
				// 3. СЦЕНАРИЙ: ПОСТ-КАРУСЕЛЬ (от 2 до 10 фото)
				else
				{
					var childrenIds = new List<string>();

					// А. Создаем отдельный контейнер для каждого фото в карусели
					foreach (var base64image in imagesBase64.Take(10))
					{
						var (mediaUrl, localPath) = await SaveMediaLocallyAsync(base64image);
						tempFilesTracker.Add(localPath);

						var itemUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";
						var itemPayload = new
						{
							media_type = "IMAGE",
							image_url = mediaUrl,
							is_carousel_item = true
						};

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

					// Б. Ждем полной готовности всех дочерних фото
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

				// 4. ФИНАЛЬНАЯ ПУБЛИКАЦИЯ ГОТОВОГО КОНТЕЙНЕРА
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
				// === ГАРАНТИРОВАННОЕ УДАЛЕНИЕ ФАЙЛОВ ===
				// Выполняется всегда, даже если произошла ошибка публикации
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
			// Получаем пути
			string tempFolder = _siteSettings.TempFolder;

			if (!Directory.Exists(tempFolder))
			{
				Directory.CreateDirectory(tempFolder);
			}

			// Определяем расширение файла из Base64 (по умолчанию .jpg)
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

			// Генерируем уникальное имя
			string fileName = $"{Guid.NewGuid()}{extension}";
			string localPath = Path.Combine(tempFolder, fileName);

			// Декодируем и сохраняем файл
			byte[] fileBytes = Convert.FromBase64String(cleanBase64);
			await File.WriteAllBytesAsync(localPath, fileBytes);

			// Формируем публичную ссылку (убедитесь, что APP_URL доступен в классе)
			// APP_URL должен быть вашим доменом на Render, например https://my-app.onrender.com
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
