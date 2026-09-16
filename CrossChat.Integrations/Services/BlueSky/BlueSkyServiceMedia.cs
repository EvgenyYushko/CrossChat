using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

		public async Task<(bool Success, string? Uri, string? Cid)> PublishPostWithVideoAsync(string caption, string base64Video, string mimeType, BlueSkyModel settings)
		{
			try
			{
				caption = await TruncateTextToMaxLength(caption);

				var videoBlob = await UploadVideoFromBase64Async(base64Video, mimeType, settings);
				if (videoBlob == null)
				{
					_logger.LogError("[BlueSky] Не удалось загрузить видео blob.");
					return (false, null, null);
				}

				var ratio = new AspectRatio { Width = 9, Height = 16 };
				return await CreatePostWithVideoAsync(caption, videoBlob, ratio, settings);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка публикации поста с видео");
				return (false, null, null);
			}
		}

		public async Task<(bool Success, string? Uri, string? Cid)> PublishPostWithImagesAsync(string caption, List<string> base64Images, BlueSkyModel settings)
		{
			try
			{
				caption = await TruncateTextToMaxLength(caption);

				if (base64Images == null || !base64Images.Any())
				{
					return await CreatePostAsync(caption, settings);
				}

				var attachments = new List<ImageAttachment>();

				foreach (var base64 in base64Images.Take(4))
				{
					string mimeType = "image/jpeg";
					if (base64.StartsWith("data:image/png") || base64.StartsWith("iVBORw"))
						mimeType = "image/png";

					var (blob, aspectRatio) = await UploadImageFromBase64Async(base64, mimeType, settings);
					if (blob != null)
					{
						attachments.Add(new ImageAttachment
						{
							Image = blob,
							AltText = "",
							AspectRatio = aspectRatio
						});
					}
				}

				if (!attachments.Any())
				{
					_logger.LogError("[BlueSky] Не удалось загрузить ни одно изображение для поста.");
					return (false, null, null);
				}

				return await CreatePostWithImagesAsync(caption, attachments, settings);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка публикации поста с фото");
				return (false, null, null);
			}
		}

		public async Task<(Blob? Blob, AspectRatio? AspectRatio)> UploadImageFromBase64Async(string base64Image, string mimeType, BlueSkyModel setting)
		{
			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var uploadUrl = $"{pdsUrl}/xrpc/com.atproto.repo.uploadBlob";

			try
			{
				string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;
				byte[] fileBytes = Convert.FromBase64String(cleanBase64);

				// Если файл больше 1.95 МБ — оптимизируем
				const int MAX_BLUESKY_BYTES = 1_950_000;
				if (fileBytes.Length > MAX_BLUESKY_BYTES)
				{
					fileBytes = CompressImageForBlueSky(fileBytes, out mimeType);
				}

				// Считываем точные пропорции изображения для идеального превью в ленте
				AspectRatio? aspectRatio = null;
				try
				{
					var info = Image.Identify(fileBytes);
					if (info != null && info.Width > 0 && info.Height > 0)
					{
						aspectRatio = new AspectRatio { Width = info.Width, Height = info.Height };
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning("[BlueSky] Не удалось определить размеры фото: {Msg}", ex.Message);
				}

				var fileContent = new ByteArrayContent(fileBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

				var response = await SendWithDPoPAsync(HttpMethod.Post, uploadUrl, setting, fileContent);
				var jsonResponse = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					var result = JsonSerializer.Deserialize<UploadBlobResponse>(jsonResponse);

					if (result?.Blob != null)
					{
						_logger.LogInformation("✅ Изображение BlueSky успешно загружено.");
						return (result.Blob, aspectRatio);
					}
				}

				_logger.LogError($"❌ Ошибка загрузки изображения BlueSky: {response.StatusCode} - {jsonResponse}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при загрузке картинки в BlueSky");
			}

			return (null, null);
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

		public async Task<(bool Success, string? Uri, string? Cid)> CreatePostWithImagesAsync(string postText, List<ImageAttachment> images, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl)) return (false, null, null);

			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			var embedPayload = new ImageEmbedPayload { Images = images };

			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				Embed = embedPayload
			};

			var payload = new { repo = setting.Did, collection = "app.bsky.feed.post", record = record };

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				string uri = doc.RootElement.GetProperty("uri").GetString()!;
				string cid = doc.RootElement.GetProperty("cid").GetString()!;

				_logger.LogInformation("✅ Пост с фото успешно опубликован в BlueSky!");
				return (true, uri, cid);
			}

			return (false, null, null);
		}

		public async Task<(bool Success, string? Uri, string? Cid)> CreatePostAsync(string postText, BlueSkyModel setting)
		{
			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
			};

			var payload = new { repo = setting.Did, collection = "app.bsky.feed.post", record = record };

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			// Отправляем через надежный DPoP вместо устаревшего Bearer
			var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				string uri = doc.RootElement.GetProperty("uri").GetString()!;
				string cid = doc.RootElement.GetProperty("cid").GetString()!;

				_logger.LogInformation("✅ Текстовый пост успешно опубликован в BlueSky!");
				return (true, uri, cid);
			}

			return (false, null, null);
		}

		/// <summary>
		/// Публикует первый комментарий (Reply) к посту в BlueSky с поддержкой кликабельных хештегов (Facets)
		/// </summary>
		public async Task<bool> CreateReplyAsync(string postText, string rootUri, string rootCid, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl))
			{
				return false;
			}

			try
			{
				postText = await TruncateTextToMaxLength(postText);
				var pdsUrl = setting.PdsUrl?.TrimEnd('/');
				var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

				// 1. Превращаем хештеги в кликабельные фасеты ATProto!
				List<Facet> facets = TryGetFacets(postText);

				// 2. Объект связи ветки (для первого комментария root и parent совпадают)
				var replyPayload = new
				{
					root = new { uri = rootUri, cid = rootCid },
					parent = new { uri = rootUri, cid = rootCid }
				};

				var record = new
				{
					text = postText,
					facets = facets.Any() ? facets : null,
					reply = replyPayload,
					createdAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
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

				// 3. Отправляем через DPoP
				var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

				if (response.IsSuccessStatusCode)
				{
					_logger.LogInformation("✅ Первый комментарий успешно опубликован в BlueSky!");
					return true;
				}

				var errorContent = await response.Content.ReadAsStringAsync();
				_logger.LogError($"❌ Ошибка публикации первого комментария в BlueSky: {response.StatusCode} - {errorContent}");
				return false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Исключение при создании первого комментария");
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
		public async Task<(bool Success, string? Uri, string? Cid)> CreatePostWithVideoAsync(string postText, Blob videoBlob, AspectRatio aspectRatio, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl)) return (false, null, null);

			var pdsUrl = setting.PdsUrl?.TrimEnd('/');
			var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

			List<Facet> facets = TryGetFacets(postText);

			var embedPayload = new VideoEmbedPayload { Video = videoBlob, AspectRatio = aspectRatio };

			var record = new PostRecord
			{
				Text = postText,
				Facets = facets.Any() ? facets : null,
				CreatedAt = DateTimeNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
				Embed = embedPayload
			};

			var payload = new { repo = setting.Did, collection = "app.bsky.feed.post", record = record };

			var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
			var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

			var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);

			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				string uri = doc.RootElement.GetProperty("uri").GetString()!;
				string cid = doc.RootElement.GetProperty("cid").GetString()!;

				_logger.LogInformation("✅ Пост с видео успешно опубликован в BlueSky!");
				return (true, uri, cid);
			}

			return (false, null, null);
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
