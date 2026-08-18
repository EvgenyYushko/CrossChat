using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossChat.Integrations.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Services
{
	public partial class BlueSkyService : IBlueSkyService
	{
		private readonly HttpClient _httpClient;
		private readonly ILogger<BlueSkyService> _logger;

		public BlueSkyService(ILogger<BlueSkyService> logger)
		{
			_httpClient = new HttpClient();
			_logger = logger;
		}

		public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string privateKeyJson)
		{
			var tokenUrl = "https://bsky.social/oauth/token";
			var clientId = "https://crosschat.ru/bluesky/client-metadata.json";

			var values = new Dictionary<string, string>
			{
				{ "grant_type", "refresh_token" },
				{ "refresh_token", refreshToken },
				{ "client_id", clientId }
			};

			try
			{
				// --- ПОПЫТКА №1 (без nonce) ---
				// 'ath' здесь не нужен, так как мы работаем с токеном обновления, а не доступа
				var (dpopProof, _) = CreateDPoPProof("POST", tokenUrl, privateKeyJson);

				var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
				{
					Content = new FormUrlEncodedContent(values)
				};
				request.Headers.Add("DPoP", dpopProof);

				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				// --- ПРОВЕРКА НА ТРЕБОВАНИЕ NONCE ---
				if (!response.IsSuccessStatusCode && json.Contains("use_dpop_nonce"))
				{
					Console.WriteLine("[BlueSky] Refresh: Сервер запросил Nonce. Повторяем...");

					if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
					{
						var serverNonce = nonceValues.First();

						// --- ПОПЫТКА №2 (с полученным nonce) ---
						var (retryDpopProof, _) = CreateDPoPProof("POST", tokenUrl, privateKeyJson, serverNonce);

						var retryRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
						{
							Content = new FormUrlEncodedContent(values)
						};
						retryRequest.Headers.Add("DPoP", retryDpopProof);

						response = await _httpClient.SendAsync(retryRequest);
						json = await response.Content.ReadAsStringAsync();
					}
				}

				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"[BlueSky] Ошибка обновления токена: {json}");
					return null;
				}

				// --- УСПЕХ! ---
				var data = JsonDocument.Parse(json).RootElement;

				return (
					data.GetProperty("access_token").GetString()!,
					data.GetProperty("refresh_token").GetString()!,
					data.GetProperty("expires_in").GetInt32()
				);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex + "[BlueSky] Критическая ошибка при RefreshToken");
				return null;
			}
		}

		public (string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null, string? accessToken = null, string? aud = null)
		{
			ECDsa ecdsa;

			if (string.IsNullOrEmpty(existingKeyJson))
			{
				// Создаем новый ключ
				ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			}
			else
			{
				// Восстанавливаем ключ из нашего DTO
				var keyDto = JsonSerializer.Deserialize<BlueSkyKeyDto>(existingKeyJson);

				// ВАЖНО: Координаты X и Y передаются через структуру ECPoint в поле Q
				var params_ = new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					D = Base64UrlEncoder.DecodeBytes(keyDto!.D),
					Q = new ECPoint
					{
						X = Base64UrlEncoder.DecodeBytes(keyDto.X),
						Y = Base64UrlEncoder.DecodeBytes(keyDto.Y)
					}
				};
				ecdsa = ECDsa.Create(params_);
			}

			var signingKey = new ECDsaSecurityKey(ecdsa);
			var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(signingKey);

			// Публичная часть для заголовка (только X и Y)
			var publicJwkDict = new Dictionary<string, object> {
				{ "kty", "EC" }, { "crv", "P-256" }, { "x", jwk.X }, { "y", jwk.Y }, { "alg", "ES256" }
			};

			var handler = new JwtSecurityTokenHandler();
			var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256));
			header["typ"] = "dpop+jwt";
			header["jwk"] = publicJwkDict;

			var payload = new JwtPayload {
				{ "jti", Guid.NewGuid().ToString("N") },
				{ "htm", method.ToUpper() },
				{ "htu", url },
				{ "iat", EpochTime.GetIntDate(DateTime.Now) }
			};

			if (!string.IsNullOrEmpty(nonce)) payload["nonce"] = nonce;

			if (!string.IsNullOrEmpty(aud))
			{
				payload["aud"] = aud;
			}

			if (!string.IsNullOrEmpty(accessToken))
			{
				using var sha256 = SHA256.Create();
				var hashBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(accessToken));
				// Кодируем хэш в Base64Url (без лишних символов)
				var ath = Base64UrlEncoder.Encode(hashBytes);
				payload["ath"] = ath;
			}

			var token = new JwtSecurityToken(header, payload);
			var proof = handler.WriteToken(token);

			// Экспортируем параметры в наш DTO для сохранения
			var p = ecdsa.ExportParameters(true);
			var exportDto = new BlueSkyKeyDto
			{
				X = Base64UrlEncoder.Encode(p.Q.X),
				Y = Base64UrlEncoder.Encode(p.Q.Y),
				D = Base64UrlEncoder.Encode(p.D)
			};
			var fullKeyJson = JsonSerializer.Serialize(exportDto);

			return (proof, fullKeyJson);
		}

		private async Task<HttpResponseMessage> SendWithDPoPAsync(HttpMethod method, string url, BlueSkyModel settings, object? body)
		{
			// Функция генерации HTTP-запроса
			async Task<HttpRequestMessage> CreateRequest(string? nonce = null)
			{
				var (proof, _) = CreateDPoPProof(method.Method, url, settings.PrivateKeyJson, nonce, settings.AccessToken, null);
				var req = new HttpRequestMessage(method, url);
				req.Headers.Add("Authorization", $"DPoP {settings.AccessToken}");
				req.Headers.Add("DPoP", proof);
				req.Headers.TryAddWithoutValidation("atproto-proxy", "did:web:api.bsky.chat#bsky_chat");

				if (body != null)
				{
					// ИСПРАВЛЕНИЕ: Если передали готовый HttpContent (байты картинки) — используем его.
					// Если передали анонимный объект (для чатов/постов) — упаковываем в JsonContent!
					if (body is HttpContent httpContent)
					{
						req.Content = httpContent;
					}
					else
					{
						req.Content = JsonContent.Create(body);
					}
				}

				return req;
			}

			// 1. Первая попытка отправки
			var request = await CreateRequest();
			var response = await _httpClient.SendAsync(request);

			// 2. Если сервер просит Nonce — запрашиваем новый Nonce и повторяем
			if (!response.IsSuccessStatusCode)
			{
				var responseContent = await response.Content.ReadAsStringAsync();
				if (responseContent.Contains("use_dpop_nonce") && response.Headers.TryGetValues("DPoP-Nonce", out var nonces))
				{
					var retryRequest = await CreateRequest(nonces.First());
					response = await _httpClient.SendAsync(retryRequest);
				}
			}

			return response;
		}

		public async Task<string> GetValidTokenAsync(BlueSkyModel settings)
		{
			// 1. Проверяем, не истек ли токен (с запасом в 2 минуты)
			if (settings.TokenExpiresAt.HasValue && settings.TokenExpiresAt.Value > DateTimeNow.AddMinutes(2))
			{
				return settings.AccessToken!;
			}

			_logger.LogInformation($"[BlueSky] Токен для @{settings.Handle} истек. Обновляем...");

			// 2. Если истек — вызываем рефреш
			var result = await RefreshTokenAsync(settings.RefreshToken!, settings.PrivateKeyJson!);

			if (result != null)
			{
				// 3. ОБЯЗАТЕЛЬНО обновляем объект в памяти
				settings.AccessToken = result.Value.AccessToken;
				settings.RefreshToken = result.Value.RefreshToken;
				settings.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);

				// 4. Сохраняем в БД (нужно будет вызвать _db.SaveChangesAsync() в вызывающем коде)
				// Но лучше передать сюда callback или сделать метод сохранения
				_logger.LogInformation($"[BlueSky] Токен успешно обновлен. Новый срок: {settings.TokenExpiresAt}");

				return settings.AccessToken;
			}

			throw new Exception("Не удалось обновить токен BlueSky. Требуется ручной перезапуск.");
		}

		public async Task<List<Convo>> GetUnreadConversationsAsync(BlueSkyModel settings)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.listConvos";

			//var accessToken = settings.AccessToken;

			try
			{
				// В SendWithDPoPAsync (который мы писали раньше) 
				// убедись, что используется правильный заголовок прокси.
				var response = await SendWithDPoPAsync(HttpMethod.Get, endpoint, settings, null);

				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					var result = JsonSerializer.Deserialize<ConvoListResponse>(json);
					return result?.Convos.Where(c => c.UnreadCount > 0).ToList() ?? new List<Convo>();
				}
				else
				{
					var err = await response.Content.ReadAsStringAsync();
					_logger.LogError($"[BlueSky] Ошибка чата: {response.StatusCode} - {err}");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Критическая ошибка GetUnreadConversations");
			}

			return new List<Convo>();
		}


		public async Task<List<MessageBlueSky>> GetMessagesAsync(BlueSkyModel settings, string convoId, int limit = 15)
		{
			// 1. Формируем URL. Важно: шлем на PDS.
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.getMessages?convoId={convoId}&limit={limit}";

			// 2. Используем наш универсальный метод с DPoP и прокси-заголовком
			var response = await SendWithDPoPAsync(HttpMethod.Get, endpoint, settings, null);

			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();

				// В BlueSky этот метод возвращает объект { "messages": [...], "cursor": "..." }
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("messages", out var messagesArray))
				{
					var messages = JsonSerializer.Deserialize<List<MessageBlueSky>>(messagesArray.GetRawText());

					// ВАЖНО: API отдает сообщения от новых к старым.
					// Для ИИ нам нужно перевернуть их, чтобы диалог шел по порядку.
					if (messages != null)
					{
						messages.Reverse();
						return messages;
					}
				}
			}
			else
			{
				var err = await response.Content.ReadAsStringAsync();
				_logger.LogError($"[BlueSky] Ошибка получения сообщений чата {convoId}: {err}");
			}

			return new List<MessageBlueSky>();
		}


		public async Task<bool> SendChatMessageAsync(BlueSkyModel settings, string convoId, string text)
		{
			// 1. ПРАВИЛЬНЫЙ URL: запрос идет на твой PDS (как и в получении списка чатов)
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.sendMessage";

			var payload = new
			{
				convoId = convoId,
				message = new { text = text }
			};

			// 2. Вызываем наш универсальный метод
			// Убедись, что внутри SendWithDPoPAsync РАСКОММЕНТИРОВАН заголовок:
			// req.Headers.TryAddWithoutValidation("atproto-proxy", "did:web:api.bsky.chat#bsky_chat");
			var response = await SendWithDPoPAsync(HttpMethod.Post, endpoint, settings, payload);

			if (response.IsSuccessStatusCode)
			{
				_logger.LogInformation($"[BlueSky] ✅ Сообщение отправлено в чат {convoId}");
				return true;
			}

			var err = await response.Content.ReadAsStringAsync();
			_logger.LogError($"[BlueSky] ❌ Ошибка отправки ЛС: {err}");
			return false;
		}

		// =================================================================
		// 3. ПОМЕТИТЬ КАК ПРОЧИТАННОЕ
		// =================================================================
		public async Task MarkConvoAsReadAsync(BlueSkyModel settings, string convoId, string lastMessageId)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.updateRead";

			var payload = new { convoId = convoId, messageId = lastMessageId };

			var response = await SendWithDPoPAsync(HttpMethod.Post, endpoint, settings, payload);
			if (response.IsSuccessStatusCode)
			{
				_logger.LogInformation($"[BlueSky] ✅ Сообщение помечено как прочитанное {convoId}");
				return;
			}

			var err = await response.Content.ReadAsStringAsync();
			_logger.LogError($"[BlueSky] ❌ Ошибка пеметки сообщеня как прочитанное: {err}");
		}
	}

	#region Models
	public class BlueSkyModel
	{
		public string AccessToken { get; set; }
		public string PrivateKeyJson { get; set; }
		public string? Handle { get; set; }
		public DateTime? TokenExpiresAt { get; set; }
		public string? RefreshToken { get; set; }
		public string Did { get; set; }
		public string PdsUrl { get; set; }
		public string SystemPrompt { get; set; }
	}

	public class BlueSkyKeyDto
	{
		public string? X { get; set; }
		public string? Y { get; set; }
		public string? D { get; set; }
	}

	public class NotificationListResponse
	{
		[JsonPropertyName("notifications")]
		public List<Notification> Notifications { get; set; } = new List<Notification>();
	}

	public class Notification
	{
		[JsonPropertyName("uri")]
		public string Uri { get; set; }        // URI комментария

		[JsonPropertyName("cid")]
		public string Cid { get; set; }        // CID комментария

		[JsonPropertyName("author")]
		public Author Author { get; set; }

		[JsonPropertyName("reason")]
		public string Reason { get; set; }     // "reply", "mention", "like" и т.д.

		[JsonPropertyName("record")]
		public object Record { get; set; }     // Внутренности поста (текст, reply refs)

		[JsonPropertyName("isRead")]
		public bool IsRead { get; set; }

		[JsonPropertyName("indexedAt")]
		public string IndexedAt { get; set; }
	}

	public class Author
	{
		[JsonPropertyName("did")]
		public string Did { get; set; }
		[JsonPropertyName("handle")]
		public string Handle { get; set; }
	}

	// Этот класс нужен, чтобы десериализовать поле "record" и найти Root поста
	public class NotificationPostRecord
	{
		[JsonPropertyName("text")]
		public string Text { get; set; }

		[JsonPropertyName("reply")]
		public ReplyRef? Reply { get; set; }

		[JsonPropertyName("$type")]
		public string Type { get; set; }
	}

	public class ReplyRef
	{
		[JsonPropertyName("root")]
		public Ref Root { get; set; }

		[JsonPropertyName("parent")]
		public Ref Parent { get; set; }
	}

	// --- DTO для Чата (Direct Messages) ---
	public class ConvoListResponse
	{
		[JsonPropertyName("convos")]
		public List<Convo> Convos { get; set; } = new List<Convo>();
	}

	public class Convo
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("unreadCount")]
		public int UnreadCount { get; set; }

		[JsonPropertyName("lastMessage")]
		public MessageBlueSky? LastMessage { get; set; }

		[JsonPropertyName("members")]
		public List<ConvoMember> Members { get; set; }
	}

	public class ConvoMember
	{
		[JsonPropertyName("did")]
		public string Did { get; set; }

		// В ответе может быть profile, handle и т.д.
	}

	public class MessageBlueSky
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }

		[JsonPropertyName("text")]
		public string Text { get; set; }

		[JsonPropertyName("sender")]
		public MessageSender Sender { get; set; }
	}

	public class MessageSender
	{
		[JsonPropertyName("did")]
		public string Did { get; set; }
	}

	public class SendMessageResponse
	{
		[JsonPropertyName("id")]
		public string Id { get; set; }
	}

	// Структура для определения диапазона символов
	public class ByteSlice
	{
		// Индекс начала (в байтах)
		[JsonPropertyName("byteStart")]
		public int ByteStart { get; set; }

		// Индекс конца (в байтах)
		[JsonPropertyName("byteEnd")]
		public int ByteEnd { get; set; }
	}

	// Структура для определения типа ссылки (Хештег)
	public class TagFeature
	{
		// Обязательный для хештегов
		[JsonPropertyName("$type")]
		public string Type { get; set; } = "app.bsky.richtext.facet#tag";

		// Само значение хештега (БЕЗ символа #)
		[JsonPropertyName("tag")]
		public string Tag { get; set; }
	}

	// Главная структура фасета
	public class Facet
	{
		// Диапазон символов в тексте
		[JsonPropertyName("index")]
		public ByteSlice Index { get; set; }

		// Определение ссылки (может быть TagFeature, LinkFeature, MentionFeature)
		[JsonPropertyName("features")]
		public List<object> Features { get; set; }
	}

	public class PostRecord
	{
		// Обязательное поле $type для записи поста
		[JsonPropertyName("$type")]
		public string Type { get; } = "app.bsky.feed.post";

		[JsonPropertyName("text")]
		public string Text { get; set; } = string.Empty;

		[JsonPropertyName("createdAt")]
		public string CreatedAt { get; set; } = string.Empty;

		// Вложение (изображения, ссылки и т.д.)
		[JsonPropertyName("embed")]
		public object? Embed { get; set; }

		[JsonPropertyName("facets")]
		public List<Facet> Facets { get; set; }

		// (Необязательные поля, такие как reply, facets, langs, здесь опущены)
	}

	public class MediaImagePayload
	{
		[JsonPropertyName("$type")]
		public string Type { get; } = "app.bsky.embed.media";

		[JsonPropertyName("media")]
		public MediaContent Media { get; set; } = new MediaContent();
	}

	public class MediaContent
	{
		[JsonPropertyName("$type")]
		public string Type { get; } = "app.bsky.embed.media.image";

		[JsonPropertyName("image")]
		public Blob Image { get; set; }

		[JsonPropertyName("alt")]
		public string AltText { get; set; } = string.Empty;
	}

	public class ImageEmbedPayload
	{
		[JsonPropertyName("$type")]
		public string Type { get; } = "app.bsky.embed.images"; // Имя свойства $type

		[JsonPropertyName("images")]
		public List<ImageAttachment> Images { get; set; } = new List<ImageAttachment>();
	}

	public class SessionResponse
	{
		// --- Ключевые поля для авторизации и PDS ---

		// Токен Доступа. Используется для всех действий (постинг, лайки и т.д.)
		[JsonPropertyName("accessJwt")]
		public string AccessJwt { get; set; } = string.Empty;

		// Токен Обновления. Используется для получения нового AccessJwt.
		[JsonPropertyName("refreshJwt")]
		public string RefreshJwt { get; set; } = string.Empty;

		// Децентрализованный Идентификатор (DID) пользователя. 
		[JsonPropertyName("did")]
		public string Did { get; set; } = string.Empty;

		// Хендл пользователя (например, alinakross.bsky.social)
		[JsonPropertyName("handle")]
		public string Handle { get; set; } = string.Empty;

		// --- Поля, связанные с DID Document (для удобства) ---

		// В AT Protocol Service Endpoint содержит URL вашего PDS.
		// Если вы десериализуете весь DID Doc, используйте этот класс:
		[JsonPropertyName("didDoc")]
		public DidDocument? DidDoc { get; set; }

		// --- Дополнительные поля ---

		[JsonPropertyName("email")]
		public string Email { get; set; } = string.Empty;

		[JsonPropertyName("emailConfirmed")]
		public bool EmailConfirmed { get; set; }

		[JsonPropertyName("active")]
		public bool Active { get; set; }
	}

	public class Service
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("type")]
		public string Type { get; set; } = string.Empty;

		// URL вашего Персонального Сервера Данных (PDS)
		[JsonPropertyName("serviceEndpoint")]
		public string ServiceEndpoint { get; set; } = string.Empty;
	}

	public class DidDocument
	{
		// Массив, содержащий URL вашего PDS
		[JsonPropertyName("service")]
		public List<Service>? Service { get; set; }

		// (Могут быть другие поля, такие как context, id, verificationMethod, но они менее критичны для автопостинга)
	}

	public class UploadBlobResponse
	{
		[JsonPropertyName("blob")]
		public Blob? Blob { get; set; }
	}

	public class AspectRatio
	{
		[JsonPropertyName("width")]
		public int Width { get; set; }

		[JsonPropertyName("height")]
		public int Height { get; set; }
	}

	// 2. Класс для вложения видео (app.bsky.embed.video)
	public class VideoEmbedPayload
	{
		[JsonPropertyName("$type")]
		public string Type { get; } = "app.bsky.embed.video";

		[JsonPropertyName("video")]
		public Blob Video { get; set; } // Blob, полученный после загрузки

		[JsonPropertyName("aspectRatio")]
		public AspectRatio? AspectRatio { get; set; }
	}

	public class Blob
	{
		// Cлужебный дескриптор, необходимый для включения в запись поста
		[JsonPropertyName("$type")]
		public string Type { get; set; } = string.Empty;

		// MIME-тип изображения (image/jpeg, image/png)
		[JsonPropertyName("mimeType")]
		public string MimeType { get; set; } = string.Empty;

		// Криптографический хэш содержимого (CID)
		[JsonPropertyName("ref")]
		public Ref? Ref { get; set; }

		// Размер файла в байтах
		[JsonPropertyName("size")]
		public long Size { get; set; }
	}

	public class Ref
	{
		// В некоторых случаях API использует $link, в других uri/cid.
		// При десериализации (чтении) записи поста (Record) структура такая:
		[JsonPropertyName("uri")]
		public string Uri { get; set; }

		[JsonPropertyName("cid")]
		public string Cid { get; set; }

		// Для совместимости со старым кодом (UploadBlob возвращает $link)
		// Можно оставить свойство Link и мапить его вручную, если нужно.
		[JsonPropertyName("$link")]
		public string Link { get; set; }
	}

	public class ImageAttachment
	{
		[JsonPropertyName("image")]
		public Blob Image { get; set; }

		[JsonPropertyName("alt")]
		public string AltText { get; set; } = string.Empty;
	}
	#endregion
}

