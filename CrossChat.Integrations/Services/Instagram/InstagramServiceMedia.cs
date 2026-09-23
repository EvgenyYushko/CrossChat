using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrossChat.Integrations.Models;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;
using File = System.IO.File;

namespace CrossChat.Integrations.Services;

public partial class InstagramService
{
	public async Task<CreateMediaResult> CreateMediaAsync(List<string> base64Strings, string accessToken, string caption = null, string? locationId = null)
	{
		if (base64Strings == null || base64Strings.Count == 0)
			throw new ArgumentException("Список изображений не может быть пустым");

		_logger.LogInformation("CreateMediaAsync - Start");
		CleanupOldTempFiles();
		var tempFilesTracker = new List<string>();

		try
		{
			ContainerResult containerResult;

			if (base64Strings.Count == 1)
			{
				containerResult = await CreateSingleMediaContainerAsync(base64Strings[0], caption, tempFilesTracker, accessToken, locationId);
			}
			else if (base64Strings.Count <= 10)
			{
				containerResult = await CreateCarouselContainerAsync(base64Strings, caption, tempFilesTracker, accessToken, locationId);
			}
			else
			{
				throw new ArgumentException("Instagram позволяет не более 10 медиа в одном посте");
			}

			if (containerResult == null || string.IsNullOrEmpty(containerResult.Id))
				throw new Exception("Не удалось создать контейнер");

			_logger.LogInformation($"Контейнер создан: {containerResult.Id}");

			var isReady = await WaitForMediaReadyAsync(containerResult.Id, accessToken);
			if (!isReady)
			{
				throw new Exception($"Медиа {containerResult.Id} не готово к публикации после ожидания");
			}

			var container = await PublishContainerAsync(containerResult.Id, accessToken);
			container.ExternalContentUrl = containerResult.ExternalContentUrl;
			return container;
		}
		finally
		{
			foreach (var localPath in tempFilesTracker)
			{
				try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
			}
		}
	}

	private async Task<bool> WaitForMediaReadyAsync(string containerId, string accessToken, int maxWaitSeconds = 120)
	{
		_logger.LogInformation($"Ожидаем готовности медиа {containerId}...");

		var startTime = DateTimeNow;

		while (DateTimeNow - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
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

	private async Task<ContainerResult> CreateSingleMediaContainerAsync(string base64String, string caption, List<string> tempFilesTracker, string accessToken, string? locationId = null)
	{
		try
		{
			_logger.LogInformation("CreateSingleMediaContainerAsync - Start. LocationId: '{LocId}'", locationId);
			string validBase64 = InstagramAspectRatioFixer.FixAspectRatioIfNeeded(base64String);

			var (mediaUrl, localPath) = await SaveMediaLocallyAsync(validBase64);
			tempFilesTracker.Add(localPath);

			await Task.Delay(500);

			// Функция сборки URL с локацией или без нее
			string BuildContainerUrl(string? locId)
			{
				// Проверяем, чтобы locationId был чисто числовым
				string locParam = (!string.IsNullOrWhiteSpace(locId) && locId.All(char.IsDigit))
					? $"&location_id={locId}"
					: "";

				if (mediaUrl.EndsWith(".mp4"))
				{
					return $"me/media?video_url={Uri.EscapeDataString(mediaUrl)}" +
						   $"&caption={Uri.EscapeDataString(caption ?? "")}" +
						   "&media_type=REELS" +
						   "&share_to_feed=true" +
						   locParam +
						   $"&access_token={accessToken}";
				}
				else
				{
					return $"me/media?image_url={Uri.EscapeDataString(mediaUrl)}" +
						   $"&caption={Uri.EscapeDataString(caption ?? "")}" +
						   locParam +
						   $"&access_token={accessToken}";
				}
			}

			string containerUrl = BuildContainerUrl(locationId);
			var response = await _httpClient.PostAsync(containerUrl, null);
			var json = await response.Content.ReadAsStringAsync();

			// СТРАХОВКА: Если Meta пожаловалась именно на location_id (ошибка 100) — повторяем БЕЗ геометки!
			if (!response.IsSuccessStatusCode && json.Contains("location_id"))
			{
				_logger.LogWarning("[Instagram] Геолокация '{LocId}' не принята Meta. Повторная отправка поста БЕЗ геометки...", locationId);
				containerUrl = BuildContainerUrl(null);
				response = await _httpClient.PostAsync(containerUrl, null);
				json = await response.Content.ReadAsStringAsync();
			}

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

	private async Task<ContainerResult> CreateCarouselContainerAsync(List<string> base64Strings, string caption, List<string> tempFilesTracker, string accessToken, string? locationId = null)
	{
		try
		{
			_logger.LogInformation("CreateCarouselContainerAsync - Start");
			var childrenIds = new List<string>();

			foreach (var base64String in base64Strings)
			{
				string validBase64 = InstagramAspectRatioFixer.FixAspectRatioIfNeeded(base64String);
				var (mediaUrl, localPath) = await SaveMediaLocallyAsync(validBase64);
				tempFilesTracker.Add(localPath);

				bool isVideo = mediaUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
				string childUrl = isVideo
					? $"me/media?video_url={Uri.EscapeDataString(mediaUrl)}&media_type=VIDEO&is_carousel_item=true&access_token={accessToken}"
					: $"me/media?image_url={Uri.EscapeDataString(mediaUrl)}&is_carousel_item=true&access_token={accessToken}";

				await Task.Delay(500);

				var childResponse = await _httpClient.PostAsync(childUrl, null);
				var childJson = await childResponse.Content.ReadAsStringAsync();

				if (!childResponse.IsSuccessStatusCode)
				{
					throw new Exception($"Не удалось создать дочерний контейнер: {childJson}");
				}

				using var childDoc = JsonDocument.Parse(childJson);
				var childId = childDoc.RootElement.GetProperty("id").GetString()!;
				childrenIds.Add(childId);

				if (isVideo)
				{
					bool isChildReady = await WaitForMediaReadyAsync(childId, accessToken, 120);
					if (!isChildReady) throw new Exception($"Видео {childId} не успело обработаться серверами Instagram.");
				}

				await Task.Delay(500);
			}

			var carouselUrl = $"me/media?access_token={accessToken}";
			var formData = new MultipartFormDataContent();
			formData.Add(new StringContent("CAROUSEL"), "media_type");
			formData.Add(new StringContent(caption ?? ""), "caption");

			// Прикрепляем геолокацию ко всей карусели
			if (!string.IsNullOrEmpty(locationId))
			{
				formData.Add(new StringContent(locationId), "location_id");
			}

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
			return new ContainerResult { Id = doc.RootElement.GetProperty("id").GetString() };
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
		string localPath = System.IO.Path.Combine(tempFolder, fileName);

		// Декодируем и сохраняем файл
		byte[] fileBytes = Convert.FromBase64String(cleanBase64);
		await File.WriteAllBytesAsync(localPath, fileBytes);

		// ЕСЛИ ЭТО ВИДЕО — УДАЛЯЕМ МЕТКУ ИИ ЧЕРЕЗ FFMPEG
		if (extension == ".mp4")
		{
			await VideoService.StripAiMetadataAsync(localPath, _logger);
		}

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

	/// <summary>
	/// Получает список последних медиа-постов пользователя из Instagram (фото и видео)
	/// </summary>
	public async Task<List<InstagramMedia>> GetUserMediaAsync(string accessToken, int limit = 50)
	{
		var result = new List<InstagramMedia>();
		string url = $"https://graph.instagram.com/v21.0/me/media?fields=id,caption,media_type,media_url,timestamp&access_token={accessToken}&limit={limit}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				var err = await response.Content.ReadAsStringAsync();
				_logger.LogError("[Instagram Media] Ошибка получения постов пользователя: {Err}", err);
				return result;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("data", out var dataElement))
			{
				foreach (var item in dataElement.EnumerateArray())
				{
					var mediaType = item.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
					var mediaUrl = item.TryGetProperty("media_url", out var mu) ? mu.GetString() : null;
					var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;

					// Нам подходят только те элементы, у которых есть прямая ссылка media_url (IMAGE или VIDEO)
					if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(mediaUrl) && (mediaType == "IMAGE" || mediaType == "VIDEO"))
					{
						result.Add(new InstagramMedia
						{
							Id = id,
							Media_Type = mediaType,
							Media_Url = mediaUrl,
							Caption = item.TryGetProperty("caption", out var c) ? c.GetString() : null
						});
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[Instagram Media] Исключение при получении медиа профиля");
		}

		return result;
	}

	/// <summary>
	/// Выбирает случайный пост профиля (фото или видео), проксирует через наш сервер и публикует в Stories
	/// </summary>
	public async Task<DailyStoryResult> PublishDailyStoryAsync(InstagramDailyStoryDto dto)
	{
		if (string.IsNullOrEmpty(dto.AccessToken))
			return new DailyStoryResult { Success = false };

		var tempFilesTracker = new List<string>();

		try
		{
			_logger.LogInformation("[Daily Story] Запуск публикации ежедневной истории для @{User}...", dto.Username);

			// 1. Получаем посты аккаунта (и фото, и видео)
			var mediaList = await GetUserMediaAsync(dto.AccessToken, limit: 100);
			if (mediaList == null || !mediaList.Any())
			{
				_logger.LogWarning("[Daily Story] В профиле @{User} не найдено подходящих медиа для сторис.", dto.Username);
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

			// 3. Находим посты, которые еще НЕ публиковались в сторис
			var availableMedia = mediaList.Where(m => !usedIds.Contains(m.Id)).ToList();

			if (availableMedia.Count == 0)
			{
				_logger.LogInformation("[Daily Story] Все посты уже были в сторис. Сбрасываем цикл постов для @{User}.", dto.Username);
				usedIds.Clear();
				availableMedia = mediaList;
			}

			// 4. Выбираем случайный пост
			var random = new Random();
			var selectedMedia = availableMedia[random.Next(availableMedia.Count)];

			bool isVideo = selectedMedia.Media_Type == "VIDEO";
			string extension = isVideo ? ".mp4" : ".jpg";
			string tempFileName = $"{Guid.NewGuid()}{extension}";
			string tempLocalPath = System.IO.Path.Combine(_siteSettings.TempFolder, tempFileName);

			_logger.LogInformation("[Daily Story] Выбран пост для истории: ID {Id} ({Type}). Подготовка файла...",
				selectedMedia.Id, selectedMedia.Media_Type);

			// 5. СКАЧИВАЕМ ФАЙЛ С CDN INSTAGRAM НА НАШ СЕРВЕР (обход ограничения First-party ICG)
			using (var downloadClient = new HttpClient())
			{
				if (isVideo)
				{
					// Скачиваем видео потоком на диск
					using var response = await downloadClient.GetAsync(selectedMedia.Media_Url, HttpCompletionOption.ResponseHeadersRead);
					response.EnsureSuccessStatusCode();

					using var fs = new FileStream(tempLocalPath, FileMode.Create, FileAccess.Write, FileShare.None);
					await response.Content.CopyToAsync(fs);
					tempFilesTracker.Add(tempLocalPath);

					// ЕСЛИ ВКЛЮЧЕН ТЕКСТ-СТИКЕР ДЛЯ СТОРИС:
					if (dto.IsStoryOverlayTextEnabled && !string.IsNullOrWhiteSpace(dto.StoryOverlayText))
					{
						_logger.LogInformation("[Daily Story] Наложение стикера на видео сторис через FFmpeg...");

						// 1. Генерируем прозрачный PNG стикер
						var (badgeBytes, yRatio) = GenerateBadgePng(dto.StoryOverlayText, 1080f);

						if (badgeBytes.Length > 0)
						{
							// 2. Накладываем на видео и одновременно вырезаем метки ИИ
							await VideoService.OverlayBadgeOnVideoAsync(
								tempLocalPath, badgeBytes, yRatio, _logger);
						}
					}
					else
					{
						// Если оверлей выключен — просто очищаем C2PA метаданные ИИ
						await VideoService.StripAiMetadataAsync(tempLocalPath, _logger);
					}
				}
				else
				{
					// Фото скачиваем в байты
					var originalImageBytes = await downloadClient.GetByteArrayAsync(selectedMedia.Media_Url);

					// Если включен текст поверх сторис — накладываем наш стильный стикер
					if (dto.IsStoryOverlayTextEnabled && !string.IsNullOrWhiteSpace(dto.StoryOverlayText))
					{
						_logger.LogInformation("[Daily Story] Наложение текста-стикера на фото сторис...");
						originalImageBytes = OverlayTextOnImage(originalImageBytes, dto.StoryOverlayText.Trim());
					}

					await File.WriteAllBytesAsync(tempLocalPath, originalImageBytes);
					tempFilesTracker.Add(tempLocalPath);
				}
			}

			// Даем диску и веб-серверу 500 мс зафиксировать файл
			await Task.Delay(500);

			// Формируем нашу независимую публичную ссылку
			string localPublicUrl = $"{_siteSettings.AppUrl.TrimEnd('/')}/temp_media/{tempFileName}";

			var mediaToPublish = new InstagramMedia
			{
				Id = selectedMedia.Id,
				Media_Type = selectedMedia.Media_Type,
				Media_Url = localPublicUrl,
				Caption = selectedMedia.Caption
			};

			// 6. Создаем контейнер и публикуем в Stories через наш URL
			var containerId = await CreateStoryContainer(mediaToPublish, dto.AccessToken);
			if (string.IsNullOrEmpty(containerId))
			{
				_logger.LogError("[Daily Story] Не удалось создать контейнер истории для @{User}", dto.Username);
				return new DailyStoryResult { Success = false };
			}

			var storyId = await WaitAndPublishContainer(containerId, dto.AccessToken);
			if (!string.IsNullOrEmpty(storyId))
			{
				_logger.LogInformation("🌟 [Daily Story] Ежедневная история успешно опубликована! StoryId: {StoryId}", storyId);

				usedIds.Add(selectedMedia.Id);
				return new DailyStoryResult
				{
					Success = true,
					StoryId = storyId,
					NewUsedMediaIdsJson = JsonSerializer.Serialize(usedIds)
				};
			}

			return new DailyStoryResult { Success = false };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[Daily Story] Ошибка при публикации ежедневной сторис для @{User}", dto.Username);
			return new DailyStoryResult { Success = false };
		}
		finally
		{
			// Гарантированно удаляем временные файлы с сервера
			foreach (var path in tempFilesTracker)
			{
				try { if (File.Exists(path)) File.Delete(path); } catch { }
			}
		}
	}

	/// <summary>
	/// Генерирует прозрачную PNG-картинку стикера с текстом для последующего наложения на видео
	/// </summary>
	private (byte[] PngBytes, float YRatio) GenerateBadgePng(string rawTextConfig, float baseWidth = 1080f)
	{
		string text = PickAndSanitizeRandomText(rawTextConfig);
		if (string.IsNullOrWhiteSpace(text)) return (Array.Empty<byte>(), 0.52f);

		FontFamily family;
		if (!SystemFonts.TryGet("Arial", out family) &&
			!SystemFonts.TryGet("DejaVu Sans", out family) &&
			!SystemFonts.TryGet("Segoe UI", out family) &&
			!SystemFonts.TryGet("Liberation Sans", out family))
		{
			family = SystemFonts.Collection.Families.FirstOrDefault();
		}

		if (family == default) return (Array.Empty<byte>(), 0.52f);

		float fontSize = Math.Clamp(baseWidth * 0.042f, 32f, 76f);
		var font = family.CreateFont(fontSize, FontStyle.Bold);

		var textOptions = new TextOptions(font);
		var textSize = TextMeasurer.MeasureSize(text, textOptions);

		float paddingX = fontSize * 1.2f;
		float paddingY = fontSize * 0.65f;
		float badgeWidth = textSize.Width + (paddingX * 2);
		float badgeHeight = textSize.Height + (paddingY * 2);

		float cornerRadius = badgeHeight / 2f;
		var rect = new RectangleF(0, 0, badgeWidth, badgeHeight);

		// Создаем абсолютно прозрачный холст точно под размер капсулы
		using var badgeImage = new Image<Rgba32>((int)Math.Ceiling(badgeWidth), (int)Math.Ceiling(badgeHeight));

		var theme = BadgeThemes[Random.Shared.Next(BadgeThemes.Length)];

		badgeImage.Mutate(ctx =>
		{
			var capsuleShape = CreateRoundedRectPath(rect, cornerRadius);
			ctx.Fill(theme.BgColor, capsuleShape);
			ctx.Draw(theme.BorderColor, 2.5f, capsuleShape);

			ctx.DrawText(text, font, theme.TextColor, new PointF(paddingX, paddingY));
		});

		using var ms = new MemoryStream();
		badgeImage.SaveAsPng(ms);

		// Случайная высота (верх, центр или низ)
		float[] yRatios = new[] { 0.22f, 0.52f, 0.75f };
		float chosenYRatio = yRatios[Random.Shared.Next(yRatios.Length)];

		return (ms.ToArray(), chosenYRatio);
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

	// Пресеты стилей для плашки (рандомизируются каждый день)
	private class StoryBadgeStyle
	{
		public Color BgColor { get; set; }
		public Color BorderColor { get; set; }
		public Color TextColor { get; set; }
	}

	private static readonly StoryBadgeStyle[] BadgeThemes = new[]
	{
		// 1. Neon Graphite (Темный полупрозрачный с неоном)
		new StoryBadgeStyle {
			BgColor = Color.FromRgba(15, 23, 42, 225),
			BorderColor = Color.FromRgba(99, 102, 241, 200),
			TextColor = Color.White
		},
		// 2. Instagram Sunset (Пурпурно-розовый)
		new StoryBadgeStyle {
			BgColor = Color.FromRgba(225, 48, 108, 230),
			BorderColor = Color.FromRgba(240, 148, 51, 220),
			TextColor = Color.White
		},
		// 3. Frosted Glass (Белое матовое стекло)
		new StoryBadgeStyle {
			BgColor = Color.FromRgba(255, 255, 255, 235),
			BorderColor = Color.FromRgba(255, 255, 255, 160),
			TextColor = Color.FromRgb(15, 23, 42) // темный текст
		},
		// 4. Cyber Violet (Фиолетовый неон)
		new StoryBadgeStyle {
			BgColor = Color.FromRgba(88, 28, 135, 230),
			BorderColor = Color.FromRgba(192, 132, 252, 210),
			TextColor = Color.White
		},
		// 5. Emerald Luxury (Изумруд с мятной обводкой)
		new StoryBadgeStyle {
			BgColor = Color.FromRgba(6, 78, 59, 230),
			BorderColor = Color.FromRgba(52, 211, 153, 210),
			TextColor = Color.White
		}
	};

	/// <summary>
	/// Выбирает случайную фразу из массива или многострочного текста и чистит от ломающих глифов
	/// </summary>
	private static string PickAndSanitizeRandomText(string rawInput)
	{
		if (string.IsNullOrWhiteSpace(rawInput)) return "";

		string selected = rawInput;

		// 1. Если задано как JSON-массив: ["текст 1", "текст 2"]
		if (rawInput.TrimStart().StartsWith("["))
		{
			try
			{
				var list = JsonSerializer.Deserialize<List<string>>(rawInput);
				if (list != null && list.Any())
				{
					selected = list[Random.Shared.Next(list.Count)];
				}
			}
			catch { }
		}
		else
		{
			// 2. Если задано через Enter с новой строки или через разделитель '|'
			var lines = rawInput
				.Split(new[] { "\r\n", "\r", "\n", "|" }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => !string.IsNullOrEmpty(l))
				.ToList();

			if (lines.Any())
			{
				selected = lines[Random.Shared.Next(lines.Count)];
			}
		}

		// 3. Защита от квадратиков: удаляем не поддерживаемые векторными шрифтами суррогатные эмодзи
		string cleanText = Regex.Replace(selected, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", "").Trim();
		return string.IsNullOrWhiteSpace(cleanText) ? selected.Trim() : cleanText;
	}

	/// <summary>
	/// Рисует случайную стильную плашку со случайным текстом и случайной позицией
	/// </summary>
	private byte[] OverlayTextOnImage(byte[] imageBytes, string rawTextConfig)
	{
		// Выбираем случайную надпись
		string text = PickAndSanitizeRandomText(rawTextConfig);
		if (string.IsNullOrWhiteSpace(text)) return imageBytes;

		using var image = Image.Load(imageBytes);

		FontFamily family;
		if (!SystemFonts.TryGet("Arial", out family) &&
			!SystemFonts.TryGet("DejaVu Sans", out family) &&
			!SystemFonts.TryGet("Segoe UI", out family) &&
			!SystemFonts.TryGet("Liberation Sans", out family))
		{
			family = SystemFonts.Collection.Families.FirstOrDefault();
		}

		if (family == default)
		{
			_logger.LogWarning("[Daily Story] Системные шрифты не найдены.");
			return imageBytes;
		}

		float fontSize = Math.Clamp(image.Width * 0.042f, 32f, 76f);
		var font = family.CreateFont(fontSize, FontStyle.Bold);

		var textOptions = new TextOptions(font);
		var textSize = TextMeasurer.MeasureSize(text, textOptions);

		float paddingX = fontSize * 1.2f;
		float paddingY = fontSize * 0.65f;
		float badgeWidth = textSize.Width + (paddingX * 2);
		float badgeHeight = textSize.Height + (paddingY * 2);

		// РАНДОМИЗАЦИЯ ПОЗИЦИИ: Верх (22%), Центр (52%) или Низ (75%)
		float[] yRatios = new[] { 0.22f, 0.52f, 0.75f };
		float chosenYRatio = yRatios[Random.Shared.Next(yRatios.Length)];

		float badgeX = (image.Width - badgeWidth) / 2f;
		float badgeY = (image.Height - badgeHeight) * chosenYRatio;

		float cornerRadius = badgeHeight / 2f;
		var rect = new RectangleF(badgeX, badgeY, badgeWidth, badgeHeight);

		float textX = badgeX + paddingX;
		float textY = badgeY + paddingY;

		// РАНДОМИЗАЦИЯ СТИЛЯ: выбираем одну из 5 тем оформления
		var theme = BadgeThemes[Random.Shared.Next(BadgeThemes.Length)];

		image.Mutate(ctx =>
		{
			var capsuleShape = CreateRoundedRectPath(rect, cornerRadius);
			ctx.Fill(theme.BgColor, capsuleShape);
			ctx.Draw(theme.BorderColor, 2.5f, capsuleShape);

			ctx.DrawText(text, font, theme.TextColor, new PointF(textX, textY));
		});

		using var ms = new MemoryStream();
		image.SaveAsJpeg(ms);
		return ms.ToArray();
	}

	/// <summary>
	/// Создает фигуру скругленного прямоугольника / капсулы через нативные дуги PathBuilder
	/// </summary>
	private static IPath CreateRoundedRectPath(RectangleF rect, float cornerRadius)
	{
		float x = rect.X;
		float y = rect.Y;
		float w = rect.Width;
		float h = rect.Height;
		float r = Math.Min(cornerRadius, Math.Min(w / 2f, h / 2f));

		var builder = new PathBuilder();
		builder.StartFigure();

		// 1. Верхняя линия и правый верхний угол
		builder.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
		builder.AddArc(x + w - r, y + r, r, r, 0, 270, 90);

		// 2. Правая линия и правый нижний угол
		builder.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
		builder.AddArc(x + w - r, y + h - r, r, r, 0, 0, 90);

		// 3. Нижняя линия и левый нижний угол
		builder.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
		builder.AddArc(x + r, y + h - r, r, r, 0, 90, 90);

		// 4. Левая линия и левый верхний угол
		builder.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
		builder.AddArc(x + r, y + r, r, r, 0, 180, 90);

		builder.CloseFigure();
		return builder.Build();
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
