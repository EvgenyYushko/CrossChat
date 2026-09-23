using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Services
{
	public partial class FaceBookService
	{
		public async Task<(bool Success, string? PostId)> PublishToPageAsync(string message, string acessToken, string pageIdToPublish, List<string> base64Images = null
			, string locationId = null)
		{
			string pageAccessToken = acessToken;

			try
			{
				using (var httpClient = new HttpClient())
				{
					if (base64Images?.Any() == true)
					{
						return await PublishAlbumAsync(pageAccessToken, pageIdToPublish, message, base64Images, locationId);
					}
					else
					{
						string publishUrl = $"https://graph.facebook.com/v24.0/{pageIdToPublish}/feed";

						var postData = new Dictionary<string, string>
						{
							{ "message", message ?? "" },
							{ "access_token", pageAccessToken }
						};

						if (!string.IsNullOrEmpty(locationId))
						{
							postData.Add("place", locationId);
						}

						using (var content = new FormUrlEncodedContent(postData))
						{
							var publishResponse = await httpClient.PostAsync(publishUrl, content);
							return await ProcessPublishResponseAsync(publishResponse);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
				return (false, null);
			}
		}

		public async Task<bool> PublishStoryAsync(string base64Image, string acessToken, string pageIdToPublish)
		{
			string pageAccessToken = acessToken;
			if (string.IsNullOrEmpty(pageAccessToken)) return false;

			using (var httpClient = new HttpClient())
			{
				// 1. Загружаем фото с published=false
				string photoId = await UploadImageAsync(pageAccessToken, pageIdToPublish, base64Image, httpClient);

				if (string.IsNullOrEmpty(photoId))
				{
					Console.WriteLine("Не удалось загрузить изображение для истории Facebook.");
					return false;
				}

				// 2. Публикуем в photo_stories (Graph API v24.0)
				string publishUrl = $"https://graph.facebook.com/v24.0/{pageIdToPublish}/photo_stories";

				var postData = new Dictionary<string, string>
				{
					{ "photo_id", photoId },
					{ "access_token", pageAccessToken }
				};

				try
				{
					using var content = new FormUrlEncodedContent(postData);
					var publishResponse = await httpClient.PostAsync(publishUrl, content);

					// Деструктурируем кортеж (Success, PostId)
					var (success, storyId) = await ProcessPublishResponseAsync(publishResponse);

					if (success)
					{
						Console.WriteLine($"История Facebook успешно опубликована! ID истории: {storyId}");
					}

					return success;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Исключение при публикации истории Facebook: {ex.Message}");
					return false;
				}
			}
		}

		public async Task<(bool Success, string? PostId)> PublishReelAsync(string message, string base64Video, string acessToken, string pageIdToPublish)
		{
			string pageAccessToken = acessToken;
			if (string.IsNullOrEmpty(pageAccessToken)) return (false, null);

			string cleanBase64 = base64Video.Contains(",") ? base64Video.Split(',')[1] : base64Video;

			byte[] videoBytes;
			try
			{
				videoBytes = Convert.FromBase64String(cleanBase64);
			}
			catch (FormatException)
			{
				Console.WriteLine("Ошибка: Неверный формат Base64 для видео.");
				return (false, null);
			}

			using (var httpClient = new HttpClient())
			{
				var (videoId, uploadUrl) = await StartReelUploadSessionAsync(pageAccessToken, pageIdToPublish, httpClient);
				if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(uploadUrl)) return (false, null);

				bool uploadSuccess = await TransferReelDataAsync(uploadUrl, videoBytes, pageAccessToken, httpClient);
				if (!uploadSuccess) return (false, null);

				return await FinishReelUploadSessionAsync(pageAccessToken, pageIdToPublish, videoId, message, httpClient);
			}
		}

		private async Task<(string videoId, string uploadUrl)> StartReelUploadSessionAsync(string pageAccessToken, string pageId, HttpClient httpClient)
		{
			// Используем конечную точку /{page-id}/video_reels
			string url = $"https://graph.facebook.com/v24.0/{pageId}/video_reels";

			var postData = new Dictionary<string, string>
			{
				// Обязательный параметр для начала сессии
				{ "upload_phase", "start" },
				{ "access_token", pageAccessToken }
			};

			using (var content = new FormUrlEncodedContent(postData))
			{
				var response = await httpClient.PostAsync(url, content);

				if (response.IsSuccessStatusCode)
				{
					string result = await response.Content.ReadAsStringAsync();
					try
					{
						// Ожидаемый ответ: {"video_id": "...", "upload_url": "..."}
						var data = JsonSerializer.Deserialize<ReelStartResponse>(result);
						Console.WriteLine($"Сессия Reel инициирована. Video ID: {data.video_id}");
						return (data.video_id, data.upload_url);
					}
					catch (JsonException ex)
					{
						Console.WriteLine($"Ошибка парсинга ответа начала сессии Reels: {ex.Message}. Ответ: {result}");
						return (null, null);
					}
				}
				else
				{
					string errorResult = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"Ошибка при начале сессии Reels: {errorResult}");
					return (null, null);
				}
			}
		}

		private async Task<bool> TransferReelDataAsync(string uploadUrl, byte[] videoBytes, string pageAccessToken, HttpClient httpClient)
		{
			// URL получен на этапе Start: https://rupload.facebook.com/video-upload/v24.0/{video-id}

			var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);

			// 1. Установка токена в заголовок Authorization, как в curl-примере.
			// Если это не сработает, вернемся к передаче токена в URL.
			request.Headers.Add("Authorization", $"OAuth {pageAccessToken}");

			// 2. Содержимое файла (Content)
			var videoContent = new ByteArrayContent(videoBytes);

			// Устанавливаем Content-Type, как требует документация: application/octet-stream
			videoContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

			// 3. ПЕРЕДАЧА НЕСТАНДАРТНЫХ ЗАГОЛОВКОВ В CONTENT.HEADERS
			// Это обходной путь для .NET, позволяющий отправить 'offset' и 'file_size' 
			// без префикса 'X-Entity-', что вызывает ошибку 'Header Offset not convertable'.
			// Мы передаем их как заголовки, связанные с содержимым.

			// Заголовок 'offset'
			videoContent.Headers.Add("offset", "0");

			// Заголовок 'file_size'
			videoContent.Headers.Add("file_size", videoBytes.Length.ToString());

			request.Content = videoContent;

			Console.WriteLine($"Загрузка Reel: URL={request.RequestUri}, Размер={videoBytes.Length} байт, Offset=0");

			var response = await httpClient.SendAsync(request);

			if (response.IsSuccessStatusCode)
			{
				string result = await response.Content.ReadAsStringAsync();
				// Ожидаемый ответ: {"success": true}
				Console.WriteLine($"Данные Reels успешно загружены. Ответ: {result}");
				return true;
			}
			else
			{
				string errorResult = await response.Content.ReadAsStringAsync();
				Console.WriteLine($"Ошибка при загрузке данных Reels: {errorResult}");
				return false;
			}
		}

		private async Task<(bool Success, string? PostId)> FinishReelUploadSessionAsync(string pageAccessToken, string pageId, string videoId, string description, HttpClient httpClient)
		{
			string url = $"https://graph.facebook.com/v24.0/{pageId}/video_reels";

			var postData = new Dictionary<string, string>
			{
				{ "upload_phase", "finish" },
				{ "video_id", videoId },
				{ "description", description ?? "" },
				{ "video_state", "PUBLISHED" },
				{ "access_token", pageAccessToken }
			};

			using (var content = new FormUrlEncodedContent(postData))
			{
				var response = await httpClient.PostAsync(url, content);
				return await ProcessPublishResponseAsync(response);
			}
		}

		public async Task<(bool Success, string? PostId)> ProcessPublishResponseAsync(HttpResponseMessage publishResponse)
		{
			if (!publishResponse.IsSuccessStatusCode)
			{
				string errorResult = await publishResponse.Content.ReadAsStringAsync();
				Console.WriteLine($"Ошибка публикации Facebook (HTTP {publishResponse.StatusCode}): {errorResult}");
				return (false, null);
			}

			try
			{
				string publishResult = await publishResponse.Content.ReadAsStringAsync();
				if (string.IsNullOrWhiteSpace(publishResult)) return (false, null);

				var data = JsonSerializer.Deserialize<PublishResponse>(publishResult);

				// post_id для Reels, id для обычных постов
				string? finalId = data?.post_id ?? data?.id;

				if (!string.IsNullOrEmpty(finalId))
				{
					Console.WriteLine($"Публикация Facebook успешна. ID поста: {finalId}");
					return (true, finalId);
				}
				else if (publishResult.Contains("\"success\":true"))
				{
					return (true, null);
				}

				return (false, null);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка десериализации ответа Facebook: {ex.Message}");
				return (false, null);
			}
		}

		private async Task<(bool Success, string? PostId)> PublishAlbumAsync(string pageAccessToken, string pageId, string message, List<string> base64Images
			, string locationId = null)
		{
			var mediaFbidList = new List<string>();

			using (var httpClient = new HttpClient())
			{
				Console.WriteLine($"Начинается загрузка {base64Images.Count} изображений в Facebook...");

				foreach (var base64Image in base64Images)
				{
					string photoId = await UploadImageAsync(pageAccessToken, pageId, base64Image, httpClient);

					if (!string.IsNullOrEmpty(photoId))
					{
						mediaFbidList.Add(photoId);
					}
					else
					{
						Console.WriteLine("Не удалось загрузить одно из изображений. Публикация отменена.");
						return (false, null);
					}
				}

				string publishUrl = $"https://graph.facebook.com/v24.0/{pageId}/feed";

				var postData = new Dictionary<string, string>
				{
					{ "message", message ?? "" },
					{ "access_token", pageAccessToken }
				};

				for (int i = 0; i < mediaFbidList.Count; i++)
				{
					var mediaObject = new { media_fbid = mediaFbidList[i] };
					string jsonMedia = JsonSerializer.Serialize(mediaObject);
					postData.Add($"attached_media[{i}]", jsonMedia);
				}

				if (!string.IsNullOrEmpty(locationId))
				{
					postData.Add("place", locationId);
				}

				using (var content = new FormUrlEncodedContent(postData))
				{
					var publishResponse = await httpClient.PostAsync(publishUrl, content);
					return await ProcessPublishResponseAsync(publishResponse);
				}
			}
		}

		private async Task<string> UploadImageAsync(string pageAccessToken, string pageId, string base64Image, HttpClient httpClient)
		{
			// ВАЖНО: Очищаем data-uri префикс ("data:image/jpeg;base64,...")
			string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;

			byte[] imageBytes;
			try
			{
				imageBytes = Convert.FromBase64String(cleanBase64);
			}
			catch (FormatException)
			{
				Console.WriteLine("Ошибка: Неверный формат Base64 для фото.");
				return null;
			}

			// Единая версия API v24.0
			string url = $"https://graph.facebook.com/v24.0/{pageId}/photos";

			using (var content = new MultipartFormDataContent())
			{
				var imageContent = new ByteArrayContent(imageBytes);
				imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

				content.Add(imageContent, "source", "image.jpg");
				content.Add(new StringContent(pageAccessToken), "access_token");
				content.Add(new StringContent("false"), "published"); // published=false для подготовки к альбому или сторис

				var response = await httpClient.PostAsync(url, content);

				if (response.IsSuccessStatusCode)
				{
					string result = await response.Content.ReadAsStringAsync();
					try
					{
						var data = JsonSerializer.Deserialize<UploadResponse>(result);
						return data?.id;
					}
					catch (JsonException)
					{
						Console.WriteLine($"Ошибка парсинга ID при загрузке. Ответ: {result}");
						return null;
					}
				}
				else
				{
					string errorResult = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"Ошибка при загрузке изображения в Facebook (HTTP {response.StatusCode}): {errorResult}");
					return null;
				}
			}
		}

		/// <summary>
		/// Получает список опубликованных фотографий страницы Facebook
		/// </summary>
		public async Task<List<(string Id, string SourceUrl)>> GetPagePhotosAsync(string pageId, string pageAccessToken, int limit = 100)
		{
			var result = new List<(string Id, string SourceUrl)>();
			string url = $"https://graph.facebook.com/v24.0/{pageId}/photos?type=uploaded&fields=id,images&access_token={pageAccessToken}&limit={limit}";

			try
			{
				using var httpClient = new HttpClient();
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode) return result;

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);

				if (doc.RootElement.TryGetProperty("data", out var dataArr))
				{
					foreach (var item in dataArr.EnumerateArray())
					{
						var id = item.GetProperty("id").GetString()!;

						// В массиве images первый элемент — это самое высокое оригинальное разрешение!
						if (item.TryGetProperty("images", out var imagesArr) && imagesArr.GetArrayLength() > 0)
						{
							var sourceUrl = imagesArr[0].GetProperty("source").GetString();
							if (!string.IsNullOrEmpty(sourceUrl))
							{
								result.Add((id, sourceUrl));
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Photos] Ошибка получения фото страницы: {ex.Message}");
			}

			return result;
		}

		/// <summary>
		/// Выбирает случайное фото страницы, накладывает стикер и публикует в Истории (Stories) Facebook Page
		/// </summary>
		public async Task<DailyStoryResult> PublishDailyStoryAsync(FacebookDailyStoryDto dto)
		{
			if (string.IsNullOrEmpty(dto.PageAccessToken) || string.IsNullOrEmpty(dto.PageId))
				return new DailyStoryResult { Success = false };

			try
			{
				Console.WriteLine($"[Facebook Daily Story] Запуск публикации авто-сторис для страницы '{dto.PageName}'...");

				// 1. Получаем фото страницы
				var photosList = await GetPagePhotosAsync(dto.PageId, dto.PageAccessToken, 100);
				if (!photosList.Any())
				{
					Console.WriteLine($"[Facebook Daily Story] На странице '{dto.PageName}' нет загруженных фото.");
					return new DailyStoryResult { Success = false };
				}

				// 2. Достаем список уже использованных ID
				var usedIds = new HashSet<string>();
				try
				{
					if (!string.IsNullOrEmpty(dto.UsedMediaIdsJson))
					{
						usedIds = JsonSerializer.Deserialize<HashSet<string>>(dto.UsedMediaIdsJson) ?? new HashSet<string>();
					}
				}
				catch { usedIds = new HashSet<string>(); }

				// 3. Отбираем неиспользованные фото
				var availablePhotos = photosList.Where(p => !usedIds.Contains(p.Id)).ToList();
				if (!availablePhotos.Any())
				{
					Console.WriteLine($"[Facebook Daily Story] Все фото уже были в историях. Сбрасываем цикл для '{dto.PageName}'.");
					usedIds.Clear();
					availablePhotos = photosList;
				}

				// 4. Выбираем случайное фото
				var selectedPhoto = availablePhotos[Random.Shared.Next(availablePhotos.Count)];
				Console.WriteLine($"[Facebook Daily Story] Выбрано фото ID: {selectedPhoto.Id}");

				// 5. Скачиваем оригинальные байты фото
				using var downloadClient = new HttpClient();
				var imageBytes = await downloadClient.GetByteArrayAsync(selectedPhoto.SourceUrl);

				// Если включен оверлей — накладываем наш дизайнерский стикер!
				if (dto.IsStoryOverlayTextEnabled && !string.IsNullOrWhiteSpace(dto.StoryOverlayText))
				{
					imageBytes = CrossChat.Infrastructure.Helpers.StoryOverlayHelper.OverlayTextOnImage(imageBytes, dto.StoryOverlayText.Trim());
				}

				string base64Image = Convert.ToBase64String(imageBytes);

				// 6. Публикуем в Истории через наш проверенный метод!
				bool success = await PublishStoryAsync(base64Image, dto.PageAccessToken, dto.PageId);

				if (success)
				{
					usedIds.Add(selectedPhoto.Id);
					return new DailyStoryResult
					{
						Success = true,
						NewUsedMediaIdsJson = JsonSerializer.Serialize(usedIds)
					};
				}

				return new DailyStoryResult { Success = false };
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Daily Story] Ошибка публикации сторис: {ex.Message}");
				return new DailyStoryResult { Success = false };
			}
		}
	}
}
