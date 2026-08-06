using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CrossChat.Integrations.Exceptions.BlueSky;
using Microsoft.Extensions.Logging;

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

				var fileContent = new ByteArrayContent(fileBytes);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

				// ИСПРАВЛЕНИЕ: Используем отправку через DPoP с подписью ключа!
				var response = await SendWithDPoPAsync(HttpMethod.Post, uploadUrl, setting, fileContent);
				var jsonResponse = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					var result = JsonSerializer.Deserialize<UploadBlobResponse>(jsonResponse);

					if (result?.Blob != null)
					{
						_logger.LogInformation("✅ Изображение BlueSky успешно загружено из Base64.");
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
				CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
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
				CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
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
