using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services
{
	public partial class XService
	{
		/// <summary>
		/// Метод для публикации текста с картинками
		/// </summary>
		public async Task<bool> CreateImagePost(string caption, List<string> base64Files, string accessToken)
		{
			// Твиттер разрешает максимум 4 картинки на один твит
			var filesToUpload = base64Files?.Take(4).ToList();

			try
			{
				var uploadedMediaIds = new List<string>();

				// 1. Загрузка картинок (V1.1 API)
				if (filesToUpload != null && filesToUpload.Any())
				{
					foreach (var base64String in filesToUpload)
					{
						try
						{
							// А. Очистка Base64
							string cleanBase64 = base64String;
							if (cleanBase64.Contains(","))
							{
								cleanBase64 = cleanBase64.Split(',')[1];
							}

							// Б. Конвертация
							byte[] imageBytes = Convert.FromBase64String(cleanBase64);

							Console.WriteLine("Загрузка изображения в X...");

							// В. Загрузка
							var uploadedMedia = await _twitterClient.Upload.UploadTweetImageAsync(imageBytes);

							if (uploadedMedia != null)
							{
								Console.WriteLine($"Фото загружено. ID: {uploadedMedia.Id}");
								uploadedMediaIds.Add(uploadedMedia.Id.ToString());
							}
						}
						catch (Exception ex)
						{
							Console.WriteLine($"Не удалось загрузить одно из фото: {ex.Message}");
						}
					}
				}

				//2. ПУБЛИКАЦИЯ ТВИТА В АККАУНТ ПОЛЬЗОВАТЕЛЯ ЧЕРЕЗ OAUTH 2.0 (Bearer)
				// Твит создается с токеном ПОЛЬЗОВАТЕЛЯ, поэтому твит появится на странице ПОЛЬЗОВАТЕЛЯ!
				var payload = new
				{
					text = caption,
					media = uploadedMediaIds.Any() ? new { media_ids = uploadedMediaIds } : null
				};

				using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/tweets")
				{
					Content = JsonContent.Create(payload)
				};
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				var response = await _httpClient.SendAsync(request);
				var responseContent = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					Console.WriteLine($"Успешно опубликовано в аккаунте пользователя! Ответ: {responseContent}");
					return true;
				}

				Console.WriteLine($"Ошибка публикации твита в аккаунт пользователя (HTTP {response.StatusCode}): {responseContent}");
				return false;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Общая ошибка метода публикации с фото: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Метод для публикации видео (MP4) из Base64 через TwitterClient + API v2
		/// </summary>
		public async Task<bool> CreateVideoPost(string caption, string base64Video, string accessToken)
		{
			if (string.IsNullOrEmpty(base64Video))
			{
				_logger.LogError("[X] Передана пустая строка Base64 для видео.");
				return false;
			}

			try
			{
				// 1. Очистка Base64 и подготовка байтов
				string cleanBase64 = base64Video.Contains(",") ? base64Video.Split(',')[1] : base64Video;
				byte[] videoBytes = Convert.FromBase64String(cleanBase64);

				_logger.LogInformation("[X] Размер видео: {Size:F2} MB. Начинаем загрузку в Twitter...", videoBytes.Length / (1024.0 * 1024.0));

				// 2. Чанковая загрузка видео через TwitterClient (V1.1 Upload API)
				var uploadedMedia = await _twitterClient.Upload.UploadTweetVideoAsync(videoBytes);

				if (uploadedMedia == null || uploadedMedia.Id == 0)
				{
					_logger.LogError("[X] Не удалось загрузить видео через TwitterClient.");
					return false;
				}

				_logger.LogInformation("[X] Видео загружено (ID: {Id}). Ожидаем процессинг...", uploadedMedia.Id);

				// 3. Ожидание обработки видео серверами Twitter
				var isProcessed = false;
				var attempts = 0;

				while (!isProcessed && attempts < 30) // До ~2-3 минут ожидания
				{
					var mediaStatus = await _twitterClient.Upload.GetVideoProcessingStatusAsync(uploadedMedia);

					// Если ProcessingInfo == null, значит видео уже готово
					if (mediaStatus?.ProcessingInfo == null)
					{
						isProcessed = true;
						break;
					}

					var state = mediaStatus.ProcessingInfo.State;

					if (state == "succeeded")
					{
						isProcessed = true;
						_logger.LogInformation("[X] Процессинг видео успешно завершен.");
						break;
					}
					else if (state == "failed")
					{
						var error = mediaStatus.ProcessingInfo.Error;
						_logger.LogError("[X] Ошибка процессинга видео Twitter: {Code} - {Msg}", error?.Code, error?.Message);
						return false;
					}
					else
					{
						attempts++;
						var waitTime = mediaStatus.ProcessingInfo.CheckAfterInMilliseconds;
						await Task.Delay(waitTime > 0 ? waitTime : 2000);
					}
				}

				if (!isProcessed)
				{
					_logger.LogError("[X] Превышено время ожидания обработки видео сервером Twitter.");
					return false;
				}

				// 4. Публикация твита V2 через Bearer токен пользователя
				var payload = new
				{
					text = caption,
					media = new { media_ids = new List<string> { uploadedMedia.Id.ToString() } }
				};

				using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/tweets")
				{
					Content = JsonContent.Create(payload)
				};
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				var response = await _httpClient.SendAsync(request);
				var responseContent = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					_logger.LogInformation("[X] ✅ Твит с видео успешно опубликован! Ответ: {Resp}", responseContent);
					return true;
				}

				_logger.LogError("[X] ❌ Ошибка публикации твита с видео (HTTP {Status}): {Resp}", response.StatusCode, responseContent);
				return false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[X] Общая ошибка метода публикации видео");
				return false;
			}
		}
	}
}
