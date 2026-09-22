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
		public async Task<(bool Success, string? PostId)> CreatePostAsync(string caption, List<string> imagesBase64, string accessToken)
		{
			CleanupOldTempFiles();

			var tempFilesTracker = new List<string>();
			try
			{
				string creationId;

				// 1. ТЕКСТОВЫЙ ПОСТ
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
						return (false, null);
					}

					var textJson = await textResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = textJson.GetProperty("id").GetString()!;

					// ИСПРАВЛЕНИЕ: Ждем готовности текстового контейнера Meta (3-5 секунд)
					bool isReady = await WaitForMediaReadyAsync(creationId, accessToken, 30);
					if (!isReady)
					{
						_logger.LogWarning($"[Threads] Текстовый контейнер {creationId} не ответил статусом готовности вовремя.");
					}
				}
				// 2. ОДИНОЧНОЕ МЕДИА (1 фото или 1 видео)
				else if (imagesBase64.Count == 1)
				{
					var (mediaUrl, localPath) = await SaveMediaLocallyAsync(imagesBase64.First());
					tempFilesTracker.Add(localPath);

					bool isVideo = mediaUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

					var singleMediaUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";

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

					await Task.Delay(500);

					var mediaResp = await _httpClient.PostAsJsonAsync(singleMediaUrl, mediaPayload);
					if (!mediaResp.IsSuccessStatusCode)
					{
						var error = await mediaResp.Content.ReadAsStringAsync();
						_logger.LogError($"[Threads] Ошибка создания медиа-контейнера: {error}");
						return (false, null);
					}

					var mediaJson = await mediaResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = mediaJson.GetProperty("id").GetString()!;

					bool isReady = await WaitForMediaReadyAsync(creationId, accessToken);
					if (!isReady) return (false, null);
				}
				// 3. КАРУСЕЛЬ
				else
				{
					var childrenIds = new List<string>();

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
							return (false, null);
						}

						var itemJson = await itemResp.Content.ReadFromJsonAsync<JsonElement>();
						string itemId = itemJson.GetProperty("id").GetString()!;
						childrenIds.Add(itemId);
					}

					foreach (var childId in childrenIds)
					{
						bool isChildReady = await WaitForMediaReadyAsync(childId, accessToken);
						if (!isChildReady) return (false, null);
					}

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
						return (false, null);
					}

					var carouselJson = await carouselResp.Content.ReadFromJsonAsync<JsonElement>();
					creationId = carouselJson.GetProperty("id").GetString()!;

					bool isCarouselReady = await WaitForMediaReadyAsync(creationId, accessToken);
					if (!isCarouselReady) return (false, null);
				}

				// 4. ФИНАЛЬНАЯ ПУБЛИКАЦИЯ С ЗАЩИТОЙ И ПОВТОРАМИ (RETRY 4279009)
				var publishUrl = $"https://graph.threads.net/v1.0/me/threads_publish?creation_id={creationId}&access_token={accessToken}";

				for (int attempt = 1; attempt <= 3; attempt++)
				{
					var publishResp = await _httpClient.PostAsync(publishUrl, null);
					var publishContent = await publishResp.Content.ReadAsStringAsync();

					if (publishResp.IsSuccessStatusCode)
					{
						using var doc = JsonDocument.Parse(publishContent);
						string publishedPostId = doc.RootElement.GetProperty("id").GetString()!;

						_logger.LogInformation($"[Threads] ✅ Пост успешно опубликован в Threads (ID поста: {publishedPostId})");
						return (true, publishedPostId);
					}

					// Если сервер Meta еще реплицирует контейнер — ждем 4 секунды и повторяем
					if (publishContent.Contains("4279009") && attempt < 3)
					{
						_logger.LogWarning($"[Threads] Сервер Meta еще не видит контейнер {creationId}. Повтор через 4 сек (попытка {attempt}/3)...");
						await Task.Delay(4000);
						continue;
					}

					_logger.LogError($"[Threads] ❌ Ошибка финальной публикации в Threads: {publishContent}");
					return (false, null);
				}

				return (false, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Threads] Критическая ошибка при публикации поста");
				return (false, null);
			}
			finally
			{
				foreach (var localPath in tempFilesTracker)
				{
					try
					{
						if (File.Exists(localPath)) File.Delete(localPath);
					}
					catch { }
				}
			}
		}

		/// <summary>
		/// Публикует первый комментарий (ответ/ветку) к посту в Threads с ожиданием готовности контейнера
		/// </summary>
		public async Task<string?> CreateReplyAsync(string replyToPostId, string text, string accessToken)
		{
			if (string.IsNullOrWhiteSpace(replyToPostId) || string.IsNullOrWhiteSpace(text))
				return null;

			try
			{
				// 1. Создаем контейнер ответа с параметром reply_to_id
				var containerUrl = $"https://graph.threads.net/v1.0/me/threads?access_token={accessToken}";
				var payload = new
				{
					media_type = "TEXT",
					text = text,
					reply_to_id = replyToPostId
				};

				var containerResp = await _httpClient.PostAsJsonAsync(containerUrl, payload);
				if (!containerResp.IsSuccessStatusCode)
				{
					var err = await containerResp.Content.ReadAsStringAsync();
					_logger.LogError($"[Threads] ❌ Ошибка создания контейнера первого комментария: {err}");
					return null;
				}

				var containerJson = await containerResp.Content.ReadFromJsonAsync<JsonElement>();
				string creationId = containerJson.GetProperty("id").GetString()!;

				// 2. ВАЖНО: Ждем, пока серверы Meta зафиксируют контейнер (даже для текста)
				bool isReady = await WaitForMediaReadyAsync(creationId, accessToken, 30);
				if (!isReady)
				{
					_logger.LogWarning($"[Threads] Контейнер комментария {creationId} не ответил статусом готовности, пробуем публикацию с повторами...");
				}

				// 3. Публикуем готовый ответ с механизмом повтора (на случай репликации Meta 4279009)
				var publishUrl = $"https://graph.threads.net/v1.0/me/threads_publish?creation_id={creationId}&access_token={accessToken}";

				for (int attempt = 1; attempt <= 3; attempt++)
				{
					var publishResp = await _httpClient.PostAsync(publishUrl, null);
					var publishContent = await publishResp.Content.ReadAsStringAsync();

					if (publishResp.IsSuccessStatusCode)
					{
						using var doc = JsonDocument.Parse(publishContent);
						string replyId = doc.RootElement.GetProperty("id").GetString()!;
						_logger.LogInformation($"[Threads] ✅ Первый комментарий успешно опубликован в Threads! ID: {replyId}");
						return replyId;
					}

					// Если сервер Meta еще не синхронизировал контейнер (ошибка 4279009) — ждем 4 секунды и повторяем
					if (publishContent.Contains("4279009") && attempt < 3)
					{
						_logger.LogWarning($"[Threads] Сервер Meta еще синхронизирует контейнер {creationId}. Повтор через 4 сек (попытка {attempt}/3)...");
						await Task.Delay(4000);
						continue;
					}

					_logger.LogError($"[Threads] ❌ Ошибка публикации первого комментария: {publishContent}");
					return null;
				}

				return null;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Threads] Ошибка при отправке первого комментария");
				return null;
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
