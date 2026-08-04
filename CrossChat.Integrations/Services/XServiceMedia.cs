using System.Net.Http.Headers;
using System.Net.Http.Json;

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
		/// Метод для публикации видео (MP4) из Base64
		/// </summary>
		public async Task<bool> CreateVideoPost(string caption, string base64Video, string accessToken)
		{
			return false;
		}
	}
}
