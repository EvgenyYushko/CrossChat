using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using File = System.IO.File;

namespace CrossChat.Integrations.Services;

public partial class InstagramService
{
	public async Task<CreateMediaResult> CreateMediaAsync(List<string> base64Strings, string accessToken, string caption = null)
	{
		if (base64Strings == null || base64Strings.Count == 0)
			throw new ArgumentException("Список изображений не может быть пустым");

		_logger.LogInformation("CreateMediaAsync - Start");

		// Запускаем фоновую чистку старого мусора (на случай прошлых падений)
		CleanupOldTempFiles();

		// Список для отслеживания созданных локальных файлов
		var tempFilesTracker = new List<string>();

		try
		{
			ContainerResult containerResult;

			if (base64Strings.Count == 1)
			{
				// Одиночное изображение
				containerResult = await CreateSingleMediaContainerAsync(base64Strings[0], caption, tempFilesTracker, accessToken);
			}
			else if (base64Strings.Count <= 10)
			{
				// Карусель
				containerResult = await CreateCarouselContainerAsync(base64Strings, caption, tempFilesTracker, accessToken);
			}
			else
			{
				throw new ArgumentException("Instagram позволяет не более 10 изображений в одном посте");
			}

			if (containerResult == null || string.IsNullOrEmpty(containerResult.Id))
				throw new Exception("Не удалось создать контейнер");

			_logger.LogInformation($"Контейнер создан: {containerResult.Id}");

			// ЖДЕМ пока медиа станет готовым к публикации
			var isReady = await WaitForMediaReadyAsync(containerResult.Id, accessToken);
			if (!isReady)
			{
				throw new Exception($"Медиа {containerResult.Id} не готово к публикации после ожидания");
			}

			_logger.LogInformation($"Медиа {containerResult.Id} готово к публикации");

			// Публикуем
			var container = await PublishContainerAsync(containerResult.Id, accessToken);
			container.ExternalContentUrl = containerResult.ExternalContentUrl;
			return container;
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

	private async Task<bool> WaitForMediaReadyAsync(string containerId, string accessToken, int maxWaitSeconds = 60)
	{
		_logger.LogInformation($"Ожидаем готовности медиа {containerId}...");

		var startTime = DateTime.Now;

		while (DateTime.Now - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
		{
			try
			{
				var statusUrl = $"{containerId}?fields=status_code,status&access_token={accessToken}";
				var response = await _httpClient.GetAsync(statusUrl);
				var json = await response.Content.ReadAsStringAsync();

				_logger.LogInformation($"Статус ответ: {json}");

				if (response.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(json);

					var statusCode = doc.RootElement.TryGetProperty("status_code", out var sc) ? sc.GetString() : null;
					var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

					_logger.LogInformation($"Статус: {status}, Status Code: {statusCode}");

					if (statusCode == "FINISHED" || status == "FINISHED")
					{
						// ДОПОЛНИТЕЛЬНАЯ ЗАДЕРЖКА после FINISHED
						_logger.LogInformation($"✅ Получен статус FINISHED, ждем 15 секунд перед публикацией...");
						await Task.Delay(15000);
						_logger.LogInformation($"✅ Медиа {containerId} готово к публикации!");
						return true;
					}
					else if (statusCode == "ERROR" || status == "ERROR")
					{
						_logger.LogError($"❌ Медиа {containerId} завершилось с ошибкой");
						return false;
					}

					_logger.LogInformation($"⏳ Медиа {containerId} еще обрабатывается...");
				}
				else
				{
					_logger.LogError($"Ошибка запроса статуса: {json}");
				}

				await Task.Delay(3000);
			}
			catch (Exception ex)
			{
				_logger.LogError($"Ошибка при проверке статуса: {ex.Message}");
				await Task.Delay(3000);
			}
		}

		_logger.LogInformation($"⏰ Таймаут ожидания медиа {containerId}");
		return false;
	}

	/// <summary>
	/// Опубликовать контейнер с медиа
	/// </summary>
	private async Task<CreateMediaResult> PublishContainerAsync(string containerId, string accessToken)
	{
		try
		{
			_logger.LogInformation($"Публикуем контейнер: {containerId}");

			var publishUrl = $"me/media_publish?creation_id={containerId}&access_token={accessToken}";
			var response = await _httpClient.PostAsync(publishUrl, null);
			var json = await response.Content.ReadAsStringAsync();

			_logger.LogInformation($"Ответ публикации: {json}");

			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"Ошибка публикации: {json}");
			}

			using var doc = JsonDocument.Parse(json);
			var mediaId = doc.RootElement.GetProperty("id").GetString();

			_logger.LogInformation($"✅ Пост успешно опубликован! ID: {mediaId}");

			return new CreateMediaResult
			{
				Id = mediaId,
				Success = true
			};
		}
		catch (Exception ex)
		{
			_logger.LogError($"❌ Ошибка в PublishContainerAsync: {ex.Message}");
			throw;
		}
	}

	private async Task<ContainerResult> CreateSingleMediaContainerAsync(string base64String, string caption, List<string> tempFilesTracker, string accessToken)
	{
		try
		{
			_logger.LogInformation("CreateSingleMediaContainerAsync - Start");

			string validBase64 = InstagramAspectRatioFixer.FixAspectRatioIfNeeded(base64String);

			// Сохраняем на свой сервер
			var (mediaUrl, localPath) = await SaveMediaLocallyAsync(validBase64);
			tempFilesTracker.Add(localPath); // Добавляем в трекер для последующего удаления

			_logger.LogInformation($"Медиа доступно по ссылке: {mediaUrl}");

			// Учитываем тип: для видео нужен параметр media_type=VIDEO, для фото по умолчанию IMAGE
			string mediaTypeParam = mediaUrl.EndsWith(".mp4") ? "&media_type=VIDEO" : "";

			// Создаем контейнер для медиа
			var containerUrl = $"me/media?image_url={Uri.EscapeDataString(mediaUrl)}" +
							   $"&caption={Uri.EscapeDataString(caption ?? "")}" +
							   mediaTypeParam +
							   $"&access_token={accessToken}";

			// Для видео Instagram ожидает video_url вместо image_url
			if (mediaUrl.EndsWith(".mp4"))
			{
				containerUrl = $"me/media?video_url={Uri.EscapeDataString(mediaUrl)}" +
							   $"&caption={Uri.EscapeDataString(caption ?? "")}" +
							   "&media_type=VIDEO" +
							   $"&access_token={accessToken}";
			}

			var response = await _httpClient.PostAsync(containerUrl, null);
			var json = await response.Content.ReadAsStringAsync();

			_logger.LogInformation($"Ответ от Instagram API: {json}");

			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"Ошибка создания контейнера: {json}");
			}

			using var doc = JsonDocument.Parse(json);
			return new ContainerResult
			{
				Id = doc.RootElement.GetProperty("id").GetString(),
				ExternalContentUrl = mediaUrl
			};
		}
		catch (Exception ex)
		{
			_logger.LogError($"Ошибка в CreateSingleMediaContainerAsync: {ex.Message}");
			throw;
		}
	}

	private async Task<ContainerResult> CreateCarouselContainerAsync(List<string> base64Strings, string caption, List<string> tempFilesTracker, string accessToken)
	{
		try
		{
			var childrenIds = new List<string>();

			// Сначала создаем все дочерние контейнеры
			foreach (var base64String in base64Strings)
			{
				string validBase64 = InstagramAspectRatioFixer.FixAspectRatioIfNeeded(base64String);

				// Сохраняем на свой сервер
				var (mediaUrl, localPath) = await SaveMediaLocallyAsync(validBase64);
				tempFilesTracker.Add(localPath); // Добавляем в трекер

				string mediaTypeParam = mediaUrl.EndsWith(".mp4") ? "&media_type=VIDEO" : "";
				var childUrl = $"me/media?image_url={Uri.EscapeDataString(mediaUrl)}{mediaTypeParam}&access_token={accessToken}";

				if (mediaUrl.EndsWith(".mp4"))
				{
					childUrl = $"me/media?video_url={Uri.EscapeDataString(mediaUrl)}&media_type=VIDEO&access_token={accessToken}";
				}

				var childResponse = await _httpClient.PostAsync(childUrl, null);
				var childJson = await childResponse.Content.ReadAsStringAsync();

				if (childResponse.IsSuccessStatusCode)
				{
					using var childDoc = JsonDocument.Parse(childJson);
					var childId = childDoc.RootElement.GetProperty("id").GetString();
					childrenIds.Add(childId);

					await Task.Delay(500); // Ждем немного между запросами
				}
				else
				{
					_logger.LogError($"Ошибка создания child: {childJson}");
					throw new Exception($"Не удалось создать дочерний контейнер: {childJson}");
				}
			}

			if (childrenIds.Count == 0)
				throw new Exception("Не удалось создать ни одного дочернего контейнера");

			var carouselUrl = $"me/media?access_token={accessToken}";

			var formData = new MultipartFormDataContent();
			formData.Add(new StringContent("CAROUSEL"), "media_type");
			formData.Add(new StringContent(caption ?? ""), "caption");

			for (int i = 0; i < childrenIds.Count; i++)
			{
				formData.Add(new StringContent(childrenIds[i]), $"children[{i}]");
			}

			var response = await _httpClient.PostAsync(carouselUrl, formData);
			var json = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				throw new HttpRequestException($"Ошибка создания карусели: {json}");
			}

			using var doc = JsonDocument.Parse(json);
			return new ContainerResult
			{
				Id = doc.RootElement.GetProperty("id").GetString()
			};
		}
		catch (Exception ex)
		{
			_logger.LogError($"Ошибка в CreateCarouselContainerAsync: {ex.Message}");
			throw;
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

	public async Task<string> PublishStoryFromBase64(string base64String, string accessToken)
	{
		if (string.IsNullOrEmpty(base64String))
		{
			_logger.LogWarning("❌ No media provided for story");
			return null;
		}

		// Запускаем фоновую чистку старого мусора (на случай прошлых падений сервера)
		CleanupOldTempFiles();

		string localFilePath = null; // Переменная для отслеживания пути к файлу для удаления

		try
		{
			string validBase64 = InstagramAspectRatioFixer.FixAspectRatioIfNeeded(base64String);

			// 1. Сохраняем файл на свой сервер (вместо ImgBB)
			var (mediaUrl, localPath) = await SaveMediaLocallyAsync(validBase64);
			localFilePath = localPath; // Запоминаем путь, чтобы удалить в finally

			if (string.IsNullOrEmpty(mediaUrl))
			{
				_logger.LogError($"❌ Не удалось получить ссылку на локальное медиа");
				return null;
			}

			_logger.LogInformation($"✅ Файл для сторис сохранен локально. Ссылка: {mediaUrl}");

			// 2. Определяем тип медиа (Instagram требует VIDEO для mp4 и IMAGE для фото)
			string mediaType = mediaUrl.EndsWith(".mp4") ? "VIDEO" : "IMAGE";

			var media = new InstagramMedia
			{
				Media_Type = mediaType,
				Media_Url = mediaUrl,
			};

			// 3. Создаем контейнер для сторис
			var containerId = await CreateStoryContainer(media, accessToken);
			if (string.IsNullOrEmpty(containerId))
			{
				_logger.LogError("❌ Не удалось создать контейнер для сторис");
				return null;
			}

			// 4. Ждем готовности медиа и публикуем
			var storyId = await WaitAndPublishContainer(containerId, accessToken);

			if (!string.IsNullOrEmpty(storyId))
			{
				_logger.LogError($"✅ Regular story published successfully: {storyId}");
				return storyId;
			}

			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "❌ Error publishing regular story");
			return null;
		}
		finally
		{
			// 5. ГАРАНТИРОВАННАЯ ОЧИСТКА
			// Этот блок выполнится всегда: и при успехе, и при ошибке (например, если Instagram отклонил файл)
			if (!string.IsNullOrEmpty(localFilePath) && File.Exists(localFilePath))
			{
				try
				{
					File.Delete(localFilePath);
					_logger.LogInformation($"🗑️ Временный файл сторис удален: {localFilePath}");
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, $"⚠️ Не удалось удалить временный файл сторис {localFilePath}");
				}
			}
		}
	}

	private async Task<string> CreateStoryContainer(InstagramMedia media, string accessToken)
	{
		string videoUrl = null;
		string imageUrl = null;

		if (media.Media_Type == "VIDEO")
		{
			videoUrl = media.Media_Url;
		}
		else
		{
			imageUrl = media.Media_Url;
		}

		var containerPayload = new
		{
			media_type = "STORIES",
			video_url = videoUrl, // Будет null, если это IMAGE
			image_url = imageUrl, // Будет null, если это VIDEO
			access_token = accessToken
		};

		var options = new JsonSerializerOptions
		{
			// КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: Не включать свойства со значением null
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true
			// Примечание: Если вы используете Newtonsoft.Json, это JsonProperty.NullValueHandling = NullValueHandling.Ignore
		};

		var containerUrl = "https://graph.instagram.com/v19.0/me/media";

		var containerJson = JsonSerializer.Serialize(containerPayload, options);
		var containerContent = new StringContent(containerJson, Encoding.UTF8, "application/json");

		using var httpClient = new HttpClient();

		var containerResponse = await httpClient.PostAsync(containerUrl, containerContent);
		var containerResponseContent = await containerResponse.Content.ReadAsStringAsync();

		if (!containerResponse.IsSuccessStatusCode)
		{
			_logger.LogError($"❌ Failed to create story container: {containerResponseContent}");
			return null;
		}

		var containerData = JsonSerializer.Deserialize<Dictionary<string, string>>(containerResponseContent);
		return containerData?["id"];
	}

	private async Task<string> WaitAndPublishContainer(string containerId, string accessToken)
	{
		var maxAttempts = 30;
		var attempt = 0;

		while (attempt < maxAttempts)
		{
			await Task.Delay(3000);

			var statusUrl = $"https://graph.instagram.com/v19.0/{containerId}?fields=status,error_message&access_token={accessToken}";
			using var httpClient = new HttpClient();
			var statusResponse = await httpClient.GetAsync(statusUrl);
			var statusContent = await statusResponse.Content.ReadAsStringAsync();

			if (statusResponse.IsSuccessStatusCode)
			{
				var statusData = JsonSerializer.Deserialize<Dictionary<string, string>>(statusContent);
				var status = statusData?["status"] ?? "";

				_logger.LogInformation($"🔄 Container status: {status}");

				if (status == "FINISHED")
				{
					// Публикуем сторис
					var publishUrl = $"https://graph.instagram.com/v19.0/me/media_publish?creation_id={containerId}&access_token={accessToken}";

					_logger.LogInformation($"📤 Publishing story to: {publishUrl}");

					var publishResponse = await httpClient.PostAsync(publishUrl, null);
					var publishResponseContent = await publishResponse.Content.ReadAsStringAsync();

					if (publishResponse.IsSuccessStatusCode)
					{
						var publishData = JsonSerializer.Deserialize<StoryPublishResponse>(publishResponseContent);
						_logger.LogInformation($"✅ Story published successfully with ID: {publishData?.Id}");
						return publishData?.Id;
					}
					else
					{
						_logger.LogError($"❌ Failed to publish story: {publishResponseContent}");
						return null;
					}
				}
				else if (status == "ERROR" || status == "EXPIRED")
				{
					var errMsg = statusData?["error_message"] ?? "";
					_logger.LogError($"❌ Container failed with status: {status}, erroreMsg: {errMsg}");
					return null;
				}
			}

			attempt++;
			_logger.LogInformation($"⏳ Attempt {attempt}/{maxAttempts} - Container not ready yet");
		}

		_logger.LogError($"❌ Container not ready after {maxAttempts} attempts");
		return null;
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
					.Where(f => f.CreationTime < DateTime.Now.AddMinutes(-15)) // Удаляем все, что старше 15 минут
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

	#region Models

	// Корневой ответ от поиска хештега
	public class HashtagSearchResponse
	{
		[JsonPropertyName("data")]
		public List<HashtagData> Data { get; set; }
	}

	// Объект с ID хештега
	public class HashtagData
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }
	}

	public class InstaResponse
	{
		[JsonPropertyName("data")]
		public List<InstaMedia> Data { get; set; }
	}

	// Данные одного поста
	public class InstaMedia
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("caption")]
		public string Caption { get; set; }

		[JsonPropertyName("media_type")]
		public string MediaType { get; set; } // IMAGE, VIDEO, CAROUSEL_ALBUM

		[JsonPropertyName("media_url")]
		public string MediaUrl { get; set; } // Ссылка на фото/видео

		[JsonPropertyName("permalink")]
		public string Permalink { get; set; } // Ссылка на пост в Instagram

		[JsonPropertyName("like_count")]
		public int LikeCount { get; set; }

		[JsonPropertyName("comments_count")]
		public int CommentsCount { get; set; }

		[JsonPropertyName("timestamp")]
		public string Timestamp { get; set; }

		// Для каруселей (альбомов)
		[JsonPropertyName("children")]
		public InstaChildren Children { get; set; }
	}

	// Обертка для вложений карусели
	public class InstaChildren
	{
		[JsonPropertyName("data")]
		public List<InstaChildMedia> Data { get; set; }
	}

	// Данные вложения (слайда)
	public class InstaChildMedia
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("media_type")]
		public string MediaType { get; set; }

		[JsonPropertyName("media_url")]
		public string MediaUrl { get; set; }
	}

	public class ContainerResult
	{
		public string Id { get; set; }
		public string ExternalContentUrl { get; set; }
	}

	public class CreateMediaResult
	{
		public string Id { get; set; }
		public bool Success { get; set; }
		public string ErrorMessage { get; set; }
		public string ExternalContentUrl { get; set; }
	}

	public class InstagramMedia
	{
		public string Id { get; set; }
		public string Caption { get; set; }
		public string Media_Type { get; set; }
		public string Media_Url { get; set; }
		public string Permalink { get; set; }
		public string Thumbnail_Url { get; set; }
		public DateTime Timestamp { get; set; }
	}

	public class MediaResponse
	{
		[JsonPropertyName("data")]
		public List<InstagramMedia> Data { get; set; }

		[JsonPropertyName("paging")]
		public Paging Paging { get; set; }
	}

	public class Paging
	{
		[JsonPropertyName("cursors")]
		public Cursors Cursors { get; set; }
	}

	public class Cursors
	{
		[JsonPropertyName("before")]
		public string Before { get; set; }

		[JsonPropertyName("after")]
		public string After { get; set; }
	}

	public class StoryPublishResponse
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }
	}

	////
	public class InstagramWebhookPayload
	{
		[JsonPropertyName("object")]
		public string Object { get; set; }

		[JsonPropertyName("entry")]
		public List<InstagramEntry> Entry { get; set; }
	}

	public class InstagramEntry
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("time")]
		public long Time { get; set; }

		[JsonPropertyName("messaging")]
		public List<InstagramMessaging> Messaging { get; set; }

		[JsonPropertyName("changes")]
		public List<InstagramChange> Changes { get; set; }
	}

	public class InstagramMessaging
	{
		[JsonPropertyName("sender")]
		public InstagramUser Sender { get; set; }

		[JsonPropertyName("recipient")]
		public InstagramUser Recipient { get; set; }

		[JsonPropertyName("timestamp")]
		public long Timestamp { get; set; }

		[JsonPropertyName("message")]
		public InstagramMessage Message { get; set; }

		[JsonPropertyName("read")]
		public InstagramRead Read { get; set; }
	}

	public class InstagramRead
	{
		[JsonPropertyName("mid")]
		public string MessageId { get; set; }
	}

	public class InstagramMessage
	{
		[JsonPropertyName("mid")]
		public string MessageId { get; set; }

		[JsonPropertyName("text")]
		public string Text { get; set; }

		[JsonPropertyName("is_echo")]
		public bool IsEcho { get; set; }

		[JsonPropertyName("attachments")]
		public List<InstagramAttachment> Attachments { get; set; }
	}

	public class InstagramAttachment
	{
		[JsonPropertyName("type")]
		public string Type { get; set; } // "image", "video", etc.

		[JsonPropertyName("payload")]
		public InstagramAttachmentPayload Payload { get; set; }
	}

	public class InstagramAttachmentPayload
	{
		[JsonPropertyName("url")]
		public string Url { get; set; }
	}

	public class InstagramUser
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("username")]
		public string Username { get; set; }

		[JsonPropertyName("self_ig_scoped_id")]
		public string SelfIgScopedId { get; set; } // Добавь это поле
	}

	public class InstagramChange
	{
		[JsonPropertyName("field")]
		public string Field { get; set; }

		[JsonPropertyName("value")]
		public JsonElement Value { get; set; } // Изменено на JsonElement для гибкости
	}

	// Модель для комментариев
	public class CommentValue
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("text")]
		public string Text { get; set; }

		[JsonPropertyName("from")]
		public InstagramUser From { get; set; }

		[JsonPropertyName("media")]
		public InstagramMedia Media { get; set; }

		[JsonPropertyName("parent_id")]
		public string ParentId { get; set; }
	}
	#endregion
}
