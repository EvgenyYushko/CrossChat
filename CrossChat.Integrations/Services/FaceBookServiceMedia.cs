using System.Text.Json;
using CrossChat.Integrations.Interfaces;

namespace CrossChat.Integrations.Services
{
	public partial class FaceBookService
	{
		public async Task<bool> PublishToPageAsync(string message, string acessToken, string pageIdToPublish, List<string> base64Images = null)
		{
			string pageAccessToken = acessToken;

			try
			{
				using (var httpClient = new HttpClient())
				{
					if (base64Images?.Any() == true)
					{
						return await PublishAlbumAsync(pageAccessToken, pageIdToPublish, message, base64Images);
					}
					else
					{
						// 3. ПУБЛИКАЦИЯ С НОВЫМ ТОКЕНОМ СТРАНИЦЫ
						string publishUrl = $"https://graph.facebook.com/v24.0/{pageIdToPublish}/feed";

						var postData = new Dictionary<string, string>
						{
							{ "message", message },
							// Передаем токен как параметр, используя FormUrlEncodedContent
							{ "access_token", pageAccessToken }
						};

						using (var content = new FormUrlEncodedContent(postData))
						{
							// 4. Отправляем POST-запрос
							var publishResponse = await httpClient.PostAsync(publishUrl, content);
							bool success = await ProcessPublishResponseAsync(publishResponse);

							return success;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
			}

			return false;
		}

		public async Task<bool> PublishStoryAsync(string base64Image, string acessToken, string pageIdToPublish)
		{
			// 1. Получаем токен страницы
			string pageAccessToken;
			try
			{
				pageAccessToken = acessToken;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при получении токена для сторис: {ex.Message}");
				return false;
			}

			using (var httpClient = new HttpClient())
			{
				// 2. Загружаем изображение (используем твой существующий метод)
				// Он загружает фото с флагом published=false, что идеально подходит для сторис.
				string photoId = await UploadImageAsync(pageAccessToken, pageIdToPublish, base64Image, httpClient);

				if (string.IsNullOrEmpty(photoId))
				{
					Console.WriteLine("Не удалось загрузить изображение для истории.");
					return false;
				}

				// 3. Публикуем загруженное фото как Историю (Story)
				// Конечная точка для фото-историй: /{page-id}/photo_stories
				string publishUrl = $"https://graph.facebook.com/v24.0/{pageIdToPublish}/photo_stories";

				var postData = new Dictionary<string, string>
				{
					{ "photo_id", photoId },
					{ "access_token", pageAccessToken }
				};

				try
				{
					using (var content = new FormUrlEncodedContent(postData))
					{
						var publishResponse = await httpClient.PostAsync(publishUrl, content);

						// Используем твой существующий метод обработки ответа
						// API вернет ID созданной истории
						bool success = await ProcessPublishResponseAsync(publishResponse);

						if (success)
						{
							Console.WriteLine("История успешно опубликована!");
						}

						return success;
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"Исключение при публикации истории: {ex.Message}");
					return false;
				}
			}
		}

		public async Task<bool> PublishReelAsync(string message, string base64Video, string acessToken, string pageIdToPublish)
		{
			// Шаг 1: Получение токена страницы (логика из PublishToPageAsync)
			string pageAccessToken;
			try
			{
				pageAccessToken = acessToken;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при получении токена страницы: {ex.Message}");
				return false;
			}

			if (string.IsNullOrEmpty(pageAccessToken)) return false;

			// Шаг 2: Конвертация Base64 в байты
			byte[] videoBytes;
			try
			{
				videoBytes = Convert.FromBase64String(base64Video);
			}
			catch (FormatException)
			{
				Console.WriteLine("Ошибка: Неверный формат Base64 для видео.");
				return false;
			}

			// Шаг 3: Выполнение 3-х шагов загрузки Reels
			using (var httpClient = new HttpClient())
			{
				// 1. Инициировать сессию
				var (videoId, uploadUrl) = await StartReelUploadSessionAsync(pageAccessToken, pageIdToPublish, httpClient);

				if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(uploadUrl)) return false;

				// 2. Загрузить видео (в вашем случае, все сразу, так как Base64 уже в памяти)
				bool uploadSuccess = await TransferReelDataAsync(uploadUrl, videoBytes, pageAccessToken, httpClient);

				if (!uploadSuccess) return false;

				// 3. Завершить и опубликовать
				bool publishSuccess = await FinishReelUploadSessionAsync(pageAccessToken, pageIdToPublish, videoId, message, httpClient);

				return publishSuccess;
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

		private async Task<bool> FinishReelUploadSessionAsync(string pageAccessToken, string pageId, string videoId, string description, HttpClient httpClient)
		{
			// Конечная точка та же, что и на старте
			string url = $"https://graph.facebook.com/v24.0/{pageId}/video_reels";

			var postData = new Dictionary<string, string>
			{
				// Обязательный параметр для завершения
				{ "upload_phase", "finish" },
				{ "video_id", videoId },
				{ "description", description },
				// Обязательные параметры для публикации
				{ "video_state", "PUBLISHED" }, // Указывает, что нужно сразу опубликовать
				{ "access_token", pageAccessToken }
			};

			using (var content = new FormUrlEncodedContent(postData))
			{
				var response = await httpClient.PostAsync(url, content);

				// Используем существующий метод для проверки ответа публикации
				return await ProcessPublishResponseAsync(response);
			}
		}

		public async Task<bool> ProcessPublishResponseAsync(HttpResponseMessage publishResponse)
		{
			// 1. Проверка статуса HTTP
			// Успешная публикация всегда вернет код 200 OK.
			if (!publishResponse.IsSuccessStatusCode)
			{
				// Если статус не 200 (например, 400 Bad Request, 403 Forbidden), 
				// это ошибка. Читаем тело для деталей (сообщение об ошибке Facebook)
				string errorResult = await publishResponse.Content.ReadAsStringAsync();
				Console.WriteLine($"Ошибка публикации (HTTP {publishResponse.StatusCode}): {errorResult}");
				return false;
			}

			// 2. Парсинг тела ответа
			try
			{
				string publishResult = await publishResponse.Content.ReadAsStringAsync();

				// Проверяем, что тело не пустое
				if (string.IsNullOrWhiteSpace(publishResult))
				{
					Console.WriteLine("Ошибка: Успешный HTTP-статус, но пустое тело ответа.");
					return false;
				}

				// Десериализуем JSON. Если Facebook вернул {"id":"..."} - это успех.
				var data = JsonSerializer.Deserialize<PublishResponse>(publishResult);

				// ПЕРВЫМ ДЕЛОМ ПРОВЕРЯЕМ post_id (для Reels), ЗАТЕМ id (для фото/текста)
				string finalId = data?.post_id ?? data?.id; // Используем post_id или id

				// 3. Проверка наличия ID
				if (!string.IsNullOrEmpty(finalId))
				{
					// УСПЕХ: Пост опубликован, и его ID получен.
					Console.WriteLine($"Публикация успешна. ID поста: {finalId}");
					return true;
				}
				else
				{
					// УСПЕХ, НО БЕЗ ID: Если пришла {"success": true} без post_id/id (например, на шаге 2 загрузки)
					if (publishResult.Contains("\"success\":true"))
					{
						Console.WriteLine($"Успешная операция, но без ID поста в ответе (возможно, это промежуточный шаг загрузки).");
						return true;
					}

					// Тело не содержит ожидаемого ID
					Console.WriteLine($"Ошибка парсинга: Успешный HTTP-статус, но отсутствует ID в ответе. Ответ: {publishResult}");
					return false;
				}
			}
			catch (JsonException ex)
			{
				// Ошибка, если тело ответа не является валидным JSON
				Console.WriteLine($"Ошибка десериализации JSON: {ex.Message}");
				return false;
			}
			catch (Exception ex)
			{
				// Прочие ошибки
				Console.WriteLine($"Неизвестная ошибка: {ex.Message}");
				return false;
			}
		}

		private async Task<bool> PublishAlbumAsync(string pageAccessToken, string pageId, string message, List<string> base64Images)
		{
			var mediaFbidList = new List<string>();

			using (var httpClient = new HttpClient())
			{
				// 1. ЗАГРУЗКА ВСЕХ ИЗОБРАЖЕНИЙ
				Console.WriteLine($"Начинается загрузка {base64Images.Count} изображений...");

				foreach (var base64Image in base64Images)
				{
					// Используем новый метод для загрузки
					string photoId = await UploadImageAsync(pageAccessToken, pageId, base64Image, httpClient);

					if (!string.IsNullOrEmpty(photoId))
					{
						mediaFbidList.Add(photoId);
					}
					else
					{
						// Если хоть одно изображение не загрузилось, прекращаем операцию.
						Console.WriteLine("Не удалось загрузить одно из изображений. Публикация отменена.");
						return false;
					}
				}

				// 2. ФОРМИРОВАНИЕ ФИНАЛЬНОГО ПОСТА (КАРУСЕЛИ)

				// Конечная точка для публикации альбома - это /feed
				string publishUrl = $"https://graph.facebook.com/v24.0/{pageId}/feed";

				var postData = new Dictionary<string, string>
				{
					{ "message", message },
					{ "access_token", pageAccessToken }
				};

				// Добавление каждого загруженного ID в формате attached_media[i]
				for (int i = 0; i < mediaFbidList.Count; i++)
				{
					// Формат значения: {"media_fbid": "ID"}
					var mediaObject = new { media_fbid = mediaFbidList[i] };
					string jsonMedia = JsonSerializer.Serialize(mediaObject);

					// Ключ: attached_media[0], attached_media[1], и т.д.
					postData.Add($"attached_media[{i}]", jsonMedia);
				}

				// 3. ОТПРАВКА ПОСТА С attached_media
				using (var content = new FormUrlEncodedContent(postData))
				{
					var publishResponse = await httpClient.PostAsync(publishUrl, content);
					return await ProcessPublishResponseAsync(publishResponse);
				}
			}
		}

		// Возвращает ID загруженной фотографии (media_fbid)
		private async Task<string> UploadImageAsync(string pageAccessToken, string pageId, string base64Image, HttpClient httpClient)
		{
			byte[] imageBytes;
			try
			{
				imageBytes = Convert.FromBase64String(base64Image);
			}
			catch (FormatException)
			{
				Console.WriteLine("Ошибка: Неверный формат Base64.");
				return null;
			}

			// Конечная точка загрузки фото для страницы
			string url = $"https://graph.facebook.com/v22.0/{pageId}/photos";

			using (var content = new MultipartFormDataContent())
			{
				var imageContent = new ByteArrayContent(imageBytes);
				imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

				// "source" - бинарное содержимое фото
				content.Add(imageContent, "source", "image.jpg");

				// Обязательные параметры передаем в теле multipart формы
				content.Add(new StringContent(pageAccessToken), "access_token");
				content.Add(new StringContent("false"), "published"); // published=false закроет немедленную публикацию в ленту

				var response = await httpClient.PostAsync(url, content);

				if (response.IsSuccessStatusCode)
				{
					string result = await response.Content.ReadAsStringAsync();
					try
					{
						var data = JsonSerializer.Deserialize<UploadResponse>(result);
						return data?.id; // Возвращаем ID загруженного фото
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
	}
}
