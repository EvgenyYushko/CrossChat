using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrossChat.Integrations.Exceptions.BlueSky;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Services
{
	public partial class BlueSkyService
	{
		private const int MAX_GRAPHEME_LENGTH = 300;

		public async Task<bool> PublishPostWithImagesAsync(string caption, List<string> base64Images, BlueSkyModel settings)
		{
			try
			{
				caption = await TruncateTextToMaxLength(caption);

				// 1. Если картинок нет — публикуем простой текстовый пост
				if (base64Images == null || !base64Images.Any())
				{
					return await CreatePostAsync(caption, settings);
				}

				// 2. Загружаем картинки (максимум 4 фото на пост в BlueSky)
				var attachments = new List<ImageAttachment>();
				foreach (var base64 in base64Images.Take(4))
				{
					string mimeType = "image/jpeg";
					if (base64.StartsWith("data:image/png") || base64.StartsWith("iVBORw"))
						mimeType = "image/png";

					var blob = await UploadImageFromBase64Async(base64, mimeType, settings);
					if (blob != null)
					{
						attachments.Add(new ImageAttachment
						{
							Image = blob,
							AltText = ""
						});
					}
				}

				if (!attachments.Any())
				{
					_logger.LogError("[BlueSky] Не удалось загрузить ни одно изображение для поста.");
					return false;
				}

				// 3. Публикуем пост с блобами картинок
				return await CreatePostWithImagesAsync(caption, attachments, settings);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка в процессе публикации поста с фото");
				return false;
			}
		}

		public async Task<Blob?> UploadImageFromBase64Async(string base64Image, string mimeType, BlueSkyModel setting)
		{
			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var uploadUrl = $"{pdsUrl}/xrpc/com.atproto.repo.uploadBlob";

			try
			{
				string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;
				byte[] fileBytes = Convert.FromBase64String(cleanBase64);

				// ВАЖНО: Лимит BlueSky для фото — ровно 2 000 000 байт!
				// Если файл больше 1.95 МБ, оптимизируем его на лету
				const int MAX_BLUESKY_BYTES = 1_950_000;
				if (fileBytes.Length > MAX_BLUESKY_BYTES)
				{
					fileBytes = CompressImageForBlueSky(fileBytes, out mimeType);
				}

				var fileContent = new ByteArrayContent(fileBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

				// Отправка через DPoP
				var response = await SendWithDPoPAsync(HttpMethod.Post, uploadUrl, setting, fileContent);
				var jsonResponse = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					var result = JsonSerializer.Deserialize<UploadBlobResponse>(jsonResponse);

					if (result?.Blob != null)
					{
						_logger.LogInformation("✅ Изображение BlueSky успешно загружено.");
						return result.Blob;
					}
				}

				_logger.LogError($"❌ Ошибка загрузки изображения BlueSky: {response.StatusCode} - {jsonResponse}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при загрузке картинки в BlueSky");
			}
			return null;
		}

		/// <summary>
		/// Автоматическая оптимизация фото под жесткий лимит BlueSky (до 2 МБ)
		/// </summary>
		private byte[] CompressImageForBlueSky(byte[] imageBytes, out string resultMimeType)
		{
			resultMimeType = "image/jpeg";
			using var image = Image.Load(imageBytes);

			// Ограничиваем максимальную сторону разумными 2048px (для BlueSky этого более чем достаточно)
			int maxDim = 2048;
			if (image.Width > maxDim || image.Height > maxDim)
			{
				image.Mutate(ctx => ctx.Resize(new ResizeOptions
				{
					Mode = ResizeMode.Max,
					Size = new Size(maxDim, maxDim)
				}));
			}

			using var ms = new MemoryStream();
			int quality = 90;

			// Подбираем качество, чтобы файл гарантированно весил меньше 1.95 МБ
			while (quality >= 60)
			{
				ms.SetLength(0);
				image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
				if (ms.Length <= 1_950_000)
				{
					return ms.ToArray();
				}
				quality -= 10;
			}

			return ms.ToArray();
		}

		public async Task<bool> CreatePostWithImagesAsync(string postText, List<ImageAttachment> images, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl))
			{
				return false;
			}
			if (images == null || images.Count == 0)
			{
				_logger.LogWarning("❌ Для данного метода требуется хотя бы одно изображение.");
				return false;
			}

			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			var embedPayload = new ImageEmbedPayload
			{
				Images = images
			};

			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				Embed = embedPayload
			};

			var payload = new
			{
				repo = setting.Did,
				collection = "app.bsky.feed.post",
				record = record
			};

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			});
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			// ИСПРАВЛЕНИЕ: Отправляем публикацию через DPoP!
			var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

			if (response.IsSuccessStatusCode)
			{
				_logger.LogInformation("✅ Пост с изображениями успешно опубликован в BlueSky!");
				return true;
			}

			var errorContent = await response.Content.ReadAsStringAsync();
			_logger.LogError($"❌ Ошибка публикации поста в BlueSky: {response.StatusCode} - {errorContent}");
			return false;
		}

		public async Task<bool> CreatePostAsync(string postText, BlueSkyModel setting)
		{
			var postEndpoint = $"{setting.PdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			// 1. Устанавливаем токен AccessJwt
			_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setting.AccessToken);

			// 2. Создаем тело запроса
			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
			};

			var payload = new
			{
				repo = setting.Did, // Используем внутренний Did
				collection = "app.bsky.feed.post",
				record = record
			};

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
			{
				WriteIndented = true,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			});
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			// 3. Отправляем запрос
			var response = await _httpClient.PostAsync(postEndpoint, content);

			if (response.IsSuccessStatusCode)
			{
				Console.WriteLine("✅ Пост успешно опубликован!");
				return true;
			}
			else
			{
				var errorContent = await response.Content.ReadAsStringAsync();
				throw new BlueSkyCreatePostException(response.StatusCode, errorContent);
			}
		}

		/// <summary>
		/// Главный метод публикации поста с видео в BlueSky
		/// </summary>
		public async Task<bool> PublishPostWithVideoAsync(string caption, string base64Video, string mimeType, BlueSkyModel settings)
		{
			try
			{
				caption = await TruncateTextToMaxLength(caption);

				// 1. Загружаем видео-блоб на PDS через DPoP
				var videoBlob = await UploadVideoFromBase64Async(base64Video, mimeType, settings);
				if (videoBlob == null)
				{
					_logger.LogError("[BlueSky] Не удалось загрузить видео-б blob в PDS.");
					return false;
				}

				// 2. Указываем пропорции (для вертикального видео 9:16, чтобы плеер Bluesky сразу выделил нужный размер)
				var ratio = new AspectRatio { Width = 9, Height = 16 };

				// 3. Создаем запись поста с видео
				return await CreatePostWithVideoAsync(caption, videoBlob, ratio, settings);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка публикации поста с видео");
				return false;
			}
		}

		/// <summary>
		/// Загрузка бинарных данных видео в репозиторий AT Protocol (PDS) с DPoP-подписью
		/// </summary>
		public async Task<Blob?> UploadVideoFromBase64Async(string base64Video, string mimeType, BlueSkyModel setting)
		{
			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var uploadUrl = $"{pdsUrl}/xrpc/com.atproto.repo.uploadBlob";

			try
			{
				// Очищаем data-uri префикс, если он есть
				string cleanBase64 = base64Video.Contains(",") ? base64Video.Split(',')[1] : base64Video;
				byte[] fileBytes = Convert.FromBase64String(cleanBase64);

				var fileContent = new ByteArrayContent(fileBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(mimeType) ? "video/mp4" : mimeType);

				// Отправляем через DPoP с подписью ключа
				var response = await SendWithDPoPAsync(HttpMethod.Post, uploadUrl, setting, fileContent);
				var jsonResponse = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					var result = JsonSerializer.Deserialize<UploadBlobResponse>(jsonResponse);

					if (result?.Blob != null)
					{
						_logger.LogInformation("✅ Видео BlueSky успешно загружено в PDS.");
						return result.Blob;
					}
				}

				_logger.LogError($"❌ Ошибка загрузки видео в BlueSky: {response.StatusCode} - {jsonResponse}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при загрузке видео в BlueSky");
			}

			return null;
		}

		/// <summary>
		/// Создание записи поста с прикрепленным видео (app.bsky.embed.video)
		/// </summary>
		public async Task<bool> CreatePostWithVideoAsync(string postText, Blob videoBlob, AspectRatio aspectRatio, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl))
			{
				return false;
			}

			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			// Формируем payload для видео по стандарту ATProto
			var embedPayload = new VideoEmbedPayload
			{
				Video = videoBlob,
				AspectRatio = aspectRatio
			};

			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				Embed = embedPayload
			};

			var payload = new
			{
				repo = setting.Did,
				collection = "app.bsky.feed.post",
				record = record
			};

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
			{
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			});
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			// Отправка через DPoP
			var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

			if (response.IsSuccessStatusCode)
			{
				_logger.LogInformation("✅ Пост с видео успешно опубликован в BlueSky!");
				return true;
			}

			var errorContent = await response.Content.ReadAsStringAsync();
			_logger.LogError($"❌ Ошибка публикации видео-поста в BlueSky: {response.StatusCode} - {errorContent}");
			return false;
		}

		private static List<Facet> TryGetFacets(string postText)
		{
			var facets = new List<Facet>();
			// Паттерн для поиска хештегов: #слово (должно быть пробел или конец строки после слова)
			var hashtagRegex = new Regex(@"#(\w+)");

			foreach (Match match in hashtagRegex.Matches(postText))
			{
				var hashtagText = match.Groups[1].Value; // Слово без #
				var matchIndex = match.Index;           // Индекс начала совпадения (включая #)

				// Вычисление смещений в БАЙТАХ
				// Bluesky требует байтовые смещения.
				var byteStart = Encoding.UTF8.GetByteCount(postText.Substring(0, matchIndex));
				var byteEnd = Encoding.UTF8.GetByteCount(postText.Substring(0, matchIndex + match.Length));

				var facet = new Facet
				{
					Index = new ByteSlice
					{
						ByteStart = byteStart,
						ByteEnd = byteEnd
					},
					Features = new List<object>
					{
						new TagFeature { Tag = hashtagText }
					}
				};
				facets.Add(facet);
			}

			return facets;
		}

		public async Task<string> TruncateTextToMaxLength(string text)
		{
			if (string.IsNullOrEmpty(text)) return text;

			try
			{
				var stringInfo = new StringInfo(text);

				// Если длина текста в символах/эмодзи вписывается в лимит — возвращаем исходный текст
				if (stringInfo.LengthInTextElements <= MAX_GRAPHEME_LENGTH)
					return text;

				// Безопасно обрезаем текст до 297 символов и добавляем многоточие "..." (всего ровно 300 символов)
				return stringInfo.SubstringByTextElements(0, MAX_GRAPHEME_LENGTH - 3) + "...";
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка при обрезке текста поста");

				// Запасной фоллбек на случай ошибки в StringInfo
				return text.Length > MAX_GRAPHEME_LENGTH ? text.Substring(0, MAX_GRAPHEME_LENGTH) : text;
			}
		}
	}
}
