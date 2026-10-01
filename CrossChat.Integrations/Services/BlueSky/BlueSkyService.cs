using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

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

		/// <summary>
		/// Получает актуальные данные профиля BlueSky (Handle, AvatarUrl, DisplayName)
		/// </summary>
		public async Task<(string? Handle, string? AvatarUrl, string? DisplayName)?> GetProfileAsync(BlueSkyModel settings)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var profileUrl = $"{pdsUrl}/xrpc/app.bsky.actor.getProfile?actor={settings.Did}";

			try
			{
				var response = await SendWithDPoPAsync(HttpMethod.Get, profileUrl, settings, null);
				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(json);
					var root = doc.RootElement;

					string? handle = root.TryGetProperty("handle", out var h) ? h.GetString() : null;
					string? avatarUrl = root.TryGetProperty("avatar", out var av) ? av.GetString() : null;
					string? displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

					return (handle, avatarUrl, displayName);
				}
				else
				{
					_logger.LogWarning("[BlueSky] Не удалось получить профиль {Did} (HTTP {Code})", settings.Did, response.StatusCode);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка получения профиля для {Did}", settings.Did);
			}

			return null;
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
				var (dpopProof, _) = CreateDPoPProof("POST", tokenUrl, privateKeyJson);

				var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
				{
					Content = new FormUrlEncodedContent(values)
				};
				request.Headers.Add("DPoP", dpopProof);

				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode && json.Contains("use_dpop_nonce"))
				{
					_logger.LogInformation("[BlueSky] Refresh: Сервер запросил Nonce. Повторяем...");

					if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
					{
						var serverNonce = nonceValues.First();
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
					_logger.LogError("[BlueSky] Ошибка обновления токена: {Json}", json);
					return null;
				}

				var data = JsonDocument.Parse(json).RootElement;

				return (
					data.GetProperty("access_token").GetString()!,
					data.GetProperty("refresh_token").GetString()!,
					data.GetProperty("expires_in").GetInt32()
				);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Критическая ошибка при RefreshToken");
				return null;
			}
		}

		public (string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null, string? accessToken = null, string? aud = null)
		{
			ECDsa ecdsa;

			if (string.IsNullOrEmpty(existingKeyJson))
			{
				ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			}
			else
			{
				var keyDto = JsonSerializer.Deserialize<BlueSkyKeyDto>(existingKeyJson);
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

			var publicJwkDict = new Dictionary<string, object> {
				{ "kty", "EC" }, { "crv", "P-256" }, { "x", jwk.X }, { "y", jwk.Y }, { "alg", "ES256" }
			};

			var handler = new JwtSecurityTokenHandler();
			var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256));
			header["typ"] = "dpop+jwt";
			header["jwk"] = publicJwkDict;

			// ВАЖНО: iat строго в UTC
			var payload = new JwtPayload {
				{ "jti", Guid.NewGuid().ToString("N") },
				{ "htm", method.ToUpper() },
				{ "htu", url },
				{ "iat", EpochTime.GetIntDate(DateTime.UtcNow) }
			};

			if (!string.IsNullOrEmpty(nonce)) payload["nonce"] = nonce;
			if (!string.IsNullOrEmpty(aud)) payload["aud"] = aud;

			if (!string.IsNullOrEmpty(accessToken))
			{
				using var sha256 = SHA256.Create();
				var hashBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(accessToken));
				var ath = Base64UrlEncoder.Encode(hashBytes);
				payload["ath"] = ath;
			}

			var token = new JwtSecurityToken(header, payload);
			var proof = handler.WriteToken(token);

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
			async Task<HttpRequestMessage> CreateRequest(string? nonce = null)
			{
				var (proof, _) = CreateDPoPProof(method.Method, url, settings.PrivateKeyJson, nonce, settings.AccessToken, null);
				var req = new HttpRequestMessage(method, url);
				req.Headers.Add("Authorization", $"DPoP {settings.AccessToken}");
				req.Headers.Add("DPoP", proof);

				if (url.Contains("/chat.bsky."))
				{
					req.Headers.TryAddWithoutValidation("atproto-proxy", "did:web:api.bsky.chat#bsky_chat");
				}
				else if (url.Contains("/app.bsky."))
				{
					req.Headers.TryAddWithoutValidation("atproto-proxy", "did:web:api.bsky.app#bsky_appview");
				}

				if (body != null)
				{
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

			var request = await CreateRequest();
			var response = await _httpClient.SendAsync(request);

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
			// Проверяем срок действия строго в UTC
			if (settings.TokenExpiresAt.HasValue && settings.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(10))
			{
				return settings.AccessToken!;
			}

			_logger.LogInformation($"[BlueSky] Токен для @{settings.Handle} требует обновления. Обновляем...");

			if (string.IsNullOrEmpty(settings.RefreshToken))
			{
				throw new InvalidOperationException($"[BlueSky] Отсутствует RefreshToken для @{settings.Handle}.");
			}

			var result = await RefreshTokenAsync(settings.RefreshToken, settings.PrivateKeyJson!);

			if (result != null)
			{
				settings.AccessToken = result.Value.AccessToken;
				settings.RefreshToken = result.Value.RefreshToken;
				// ВАЖНО: сохраняем срок жизни строго в UTC
				settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);

				_logger.LogInformation($"[BlueSky] Токен успешно обновлен для @{settings.Handle}. Истекает: {settings.TokenExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");

				return settings.AccessToken;
			}

			throw new Exception($"Не удалось обновить токен BlueSky для @{settings.Handle}.");
		}

		public async Task<List<Convo>> GetUnreadConversationsAsync(BlueSkyModel settings)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.listConvos";

			try
			{
				var response = await SendWithDPoPAsync(HttpMethod.Get, endpoint, settings, null);
				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					var result = JsonSerializer.Deserialize<ConvoListResponse>(json);
					return result?.Convos.Where(c => c.UnreadCount > 0).ToList() ?? new List<Convo>();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка GetUnreadConversations");
			}

			return new List<Convo>();
		}

		public async Task<List<MessageBlueSky>> GetMessagesAsync(BlueSkyModel settings, string convoId, int limit = 15)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.getMessages?convoId={convoId}&limit={limit}";

			var response = await SendWithDPoPAsync(HttpMethod.Get, endpoint, settings, null);

			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				if (doc.RootElement.TryGetProperty("messages", out var messagesArray))
				{
					var messages = JsonSerializer.Deserialize<List<MessageBlueSky>>(messagesArray.GetRawText());
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
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/chat.bsky.convo.sendMessage";

			var payload = new
			{
				convoId = convoId,
				message = new { text = text }
			};

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

		public async Task<List<Notification>> GetUnreadNotificationsAsync(BlueSkyModel settings)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/app.bsky.notification.listNotifications?limit=25";

			try
			{
				var response = await SendWithDPoPAsync(HttpMethod.Get, endpoint, settings, null);
				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
					var result = JsonSerializer.Deserialize<NotificationListResponse>(json, options);

					return result?.Notifications?
						.Where(n => n.Reason == "reply" || n.Reason == "mention")
						.ToList() ?? new List<Notification>();
				}
				else
				{
					var err = await response.Content.ReadAsStringAsync();
					_logger.LogError("[BlueSky] Ошибка запроса уведомлений (HTTP {Code}): {Err}", response.StatusCode, err);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Исключение при получении уведомлений");
			}

			return new List<Notification>();
		}

		public async Task<bool> ReplyToThreadCommentAsync(string postText, string parentUri, string parentCid, string rootUri, string rootCid, BlueSkyModel setting)
		{
			if (string.IsNullOrEmpty(setting.AccessToken) || string.IsNullOrEmpty(setting.PdsUrl)) return false;

			try
			{
				postText = await TruncateTextToMaxLength(postText);
				var pdsUrl = setting.PdsUrl?.TrimEnd('/');
				var postEndpoint = $"{pdsUrl}/xrpc/com.atproto.repo.createRecord";

				List<Facet> facets = TryGetFacets(postText);

				var replyPayload = new
				{
					root = new { uri = rootUri, cid = rootCid },
					parent = new { uri = parentUri, cid = parentCid }
				};

				var record = new
				{
					text = postText,
					facets = facets.Any() ? facets : null,
					reply = replyPayload,
					// ИСПРАВЛЕНО: строго UTC дата!
					createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
				};

				var payload = new
				{
					repo = setting.Did,
					collection = "app.bsky.feed.post",
					record = record
				};

				var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
				var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

				var response = await SendWithDPoPAsync(HttpMethod.Post, postEndpoint, setting, content);
				return response.IsSuccessStatusCode;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка отправки ответа на комментарий");
				return false;
			}
		}

		public async Task UpdateNotificationsSeenAsync(BlueSkyModel settings, DateTime seenAt)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var endpoint = $"{pdsUrl}/xrpc/app.bsky.notification.updateSeen";

			// ИСПРАВЛЕНО: строго UTC
			DateTime utcTime = seenAt.Kind == DateTimeKind.Utc ? seenAt : seenAt.ToUniversalTime();
			string utcString = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

			var payload = new { seenAt = utcString };
			await SendWithDPoPAsync(HttpMethod.Post, endpoint, settings, payload);
		}

		/// <summary>
		/// Получает расширенный профиль аккаунта BlueSky со счетчиками подписчиков, подписок и постов
		/// </summary>
		public async Task<BlueSkyFullProfileDto?> GetFullProfileAsync(BlueSkyModel settings)
		{
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');
			var profileUrl = $"{pdsUrl}/xrpc/app.bsky.actor.getProfile?actor={settings.Did}";

			try
			{
				var response = await SendWithDPoPAsync(HttpMethod.Get, profileUrl, settings, null);
				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(json);
					var root = doc.RootElement;

					return new BlueSkyFullProfileDto
					{
						Did = root.GetProperty("did").GetString() ?? settings.Did,
						Handle = root.TryGetProperty("handle", out var h) ? h.GetString() ?? settings.Handle ?? "" : settings.Handle ?? "",
						DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
						AvatarUrl = root.TryGetProperty("avatar", out var av) ? av.GetString() : null,
						FollowersCount = root.TryGetProperty("followersCount", out var fc) ? fc.GetInt32() : 0,
						FollowsCount = root.TryGetProperty("followsCount", out var fwc) ? fwc.GetInt32() : 0,
						PostsCount = root.TryGetProperty("postsCount", out var pc) ? pc.GetInt32() : 0
					};
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка получения профиля для {Did}", settings.Did);
			}

			return null;
		}

		/// <summary>
		/// Получает ленту постов автора в BlueSky с лайками, репостами и реплаями за 1 сетевой запрос
		/// </summary>
		public async Task<BlueSkyFeedPageDto> GetAuthorFeedAsync(BlueSkyModel settings, int limit = 24, string? cursor = null)
		{
			var result = new BlueSkyFeedPageDto();
			var pdsUrl = settings.PdsUrl?.TrimEnd('/');

			// Запрашиваем посты автора (filter=posts_and_author_threads исключает чужие ответы)
			var url = $"{pdsUrl}/xrpc/app.bsky.feed.getAuthorFeed?actor={settings.Did}&limit={limit}&filter=posts_and_author_threads";
			if (!string.IsNullOrEmpty(cursor))
			{
				url += $"&cursor={Uri.EscapeDataString(cursor)}";
			}

			try
			{
				var response = await SendWithDPoPAsync(HttpMethod.Get, url, settings, null);
				if (!response.IsSuccessStatusCode)
				{
					var err = await response.Content.ReadAsStringAsync();
					_logger.LogError("[BlueSky Analytics] Ошибка загрузки ленты: {Err}", err);
					return result;
				}

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (root.TryGetProperty("feed", out var feedArr))
				{
					foreach (var item in feedArr.EnumerateArray())
					{
						if (!item.TryGetProperty("post", out var postElem)) continue;

						var postUri = postElem.GetProperty("uri").GetString()!;
						var postCid = postElem.GetProperty("cid").GetString()!;

						string? text = null;
						DateTime timestamp = DateTime.UtcNow;

						if (postElem.TryGetProperty("record", out var recordElem))
						{
							if (recordElem.TryGetProperty("text", out var t)) text = t.GetString();
							if (recordElem.TryGetProperty("createdAt", out var ca) && DateTime.TryParse(ca.GetString(), out var dt))
							{
								timestamp = dt.ToUniversalTime();
							}
						}

						// Счётчики вовлеченности
						int likes = postElem.TryGetProperty("likeCount", out var lc) ? lc.GetInt32() : 0;
						int reposts = postElem.TryGetProperty("repostCount", out var rc) ? rc.GetInt32() : 0;
						int replies = postElem.TryGetProperty("replyCount", out var rpc) ? rpc.GetInt32() : 0;
						int quotes = postElem.TryGetProperty("quoteCount", out var qc) ? qc.GetInt32() : 0;

						// Определение медиа
						string mediaType = "TEXT";
						string? mediaUrl = null;
						string? thumbUrl = null;

						if (postElem.TryGetProperty("embed", out var embedElem))
						{
							var embedType = embedElem.TryGetProperty("$type", out var et) ? et.GetString() ?? "" : "";

							// Картинки
							if (embedType.Contains("embed.images") && embedElem.TryGetProperty("images", out var imgArr) && imgArr.GetArrayLength() > 0)
							{
								mediaType = "IMAGE";
								var firstImg = imgArr[0];
								thumbUrl = firstImg.TryGetProperty("thumb", out var th) ? th.GetString() : null;
								mediaUrl = firstImg.TryGetProperty("fullsize", out var fs) ? fs.GetString() : thumbUrl;
							}
							// Видео
							else if (embedType.Contains("embed.video"))
							{
								mediaType = "VIDEO";
								thumbUrl = embedElem.TryGetProperty("thumbnail", out var vth) ? vth.GetString() : null;
								mediaUrl = thumbUrl;
							}
						}

						// Формируем красивую веб-ссылку: https://bsky.app/profile/{handle}/post/{rkey}
						string rkey = postUri.Split('/').Last();
						string handle = settings.Handle ?? settings.Did;
						string permalink = $"https://bsky.app/profile/{handle}/post/{rkey}";

						result.Posts.Add(new BlueSkyFeedPostDto
						{
							Uri = postUri,
							Cid = postCid,
							Text = text,
							MediaType = mediaType,
							MediaUrl = mediaUrl,
							ThumbnailUrl = thumbUrl,
							Permalink = permalink,
							Timestamp = timestamp,
							LikesCount = likes,
							RepostsCount = reposts,
							RepliesCount = replies,
							QuotesCount = quotes
						});
					}
				}

				if (root.TryGetProperty("cursor", out var cProp))
				{
					result.Cursor = cProp.GetString();
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky Analytics] Исключение при парсинге ленты");
			}

			return result;
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
		public Blob Image { get; set; } = null!;

		[JsonPropertyName("alt")]
		public string AltText { get; set; } = string.Empty;

		// ВОТ ЭТО свойство решает проблему с полями в ленте:
		[JsonPropertyName("aspectRatio")]
		public AspectRatio? AspectRatio { get; set; }
	}
	#endregion
}

