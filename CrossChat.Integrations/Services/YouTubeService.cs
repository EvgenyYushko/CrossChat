using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models; // <-- Подключаем свои локальные модели!
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossChat.Integrations.Services
{
	public class YouTubeService : IYouTubeService
	{
		private readonly HttpClient _httpClient;
		private readonly ILogger<YouTubeService> _logger;
		private readonly YouTubeOptions _options;

		public YouTubeService(
			HttpClient httpClient,
			ILogger<YouTubeService> logger,
			IOptions<YouTubeOptions> options) // <-- Принимаем YouTubeOptions
		{
			_httpClient = httpClient;
			_logger = logger;
			_options = options.Value;
		}

		public string GetAuthorizationUrl(string state, string redirectUri)
		{
			var scopes = new[]
			{
				"https://www.googleapis.com/auth/youtube.readonly",
				"https://www.googleapis.com/auth/youtube.upload",
				"openid",
				"profile",
				"email"
			};

			var scopeString = Uri.EscapeDataString(string.Join(" ", scopes));

			return $"https://accounts.google.com/o/oauth2/v2/auth?" +
				   $"client_id={_options.ClientId}&" + // <-- используем _options.ClientId
				   $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
				   $"response_type=code&" +
				   $"scope={scopeString}&" +
				   $"access_type=offline&" +
				   $"prompt=select_account%20consent&" +
				   $"state={state}";
		}

		public async Task<(string AccessToken, string? RefreshToken, int ExpiresIn)?> ExchangeCodeForTokensAsync(string code, string redirectUri)
		{
			const string tokenEndpoint = "https://oauth2.googleapis.com/token";

			var postData = new Dictionary<string, string>
			{
				{ "code", code },
				{ "client_id", _options.ClientId },           // <-- _options.ClientId
				{ "client_secret", _options.ClientSecret },   // <-- _options.ClientSecret
				{ "redirect_uri", redirectUri },
				{ "grant_type", "authorization_code" }
			};

			try
			{
				var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(postData));
				var json = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("[YouTube] Ошибка обмена кода на токены: {Json}", json);
					return null;
				}

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				string accessToken = root.GetProperty("access_token").GetString()!;
				string? refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
				int expiresIn = root.GetProperty("expires_in").GetInt32();

				return (accessToken, refreshToken, expiresIn);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[YouTube] Исключение при обмене токенов");
				return null;
			}
		}

		public async Task<(string? AccessToken, int ExpiresIn)?> RefreshAccessTokenAsync(string refreshToken)
		{
			const string tokenEndpoint = "https://oauth2.googleapis.com/token";

			var postData = new Dictionary<string, string>
			{
				{ "client_id", _options.ClientId },           // <-- _options.ClientId
				{ "client_secret", _options.ClientSecret },   // <-- _options.ClientSecret
				{ "refresh_token", refreshToken },
				{ "grant_type", "refresh_token" }
			};

			try
			{
				var response = await _httpClient.PostAsync(tokenEndpoint, new FormUrlEncodedContent(postData));
				var json = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("[YouTube] Ошибка рефреша токена: {Json}", json);
					return null;
				}

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				string accessToken = root.GetProperty("access_token").GetString()!;
				int expiresIn = root.GetProperty("expires_in").GetInt32();

				return (accessToken, expiresIn);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[YouTube] Исключение при рефреше токена");
				return null;
			}
		}

		public async Task<YouTubeChannelInfoDto?> GetChannelInfoAsync(string accessToken)
		{
			var url = "https://www.googleapis.com/youtube/v3/channels?part=snippet,statistics&mine=true";

			try
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				var response = await _httpClient.SendAsync(req);
				var json = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError("[YouTube] Ошибка запроса информации о канале: {Json}", json);
					return null;
				}

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
				{
					_logger.LogWarning("[YouTube] У данного Google-аккаунта не найдено YouTube каналов.");
					return null;
				}

				var channelItem = items[0];
				var channelId = channelItem.GetProperty("id").GetString()!;
				var snippet = channelItem.GetProperty("snippet");
				var title = snippet.GetProperty("title").GetString()!;
				string? customUrl = snippet.TryGetProperty("customUrl", out var cu) ? cu.GetString() : null;

				string? avatarUrl = null;
				if (snippet.TryGetProperty("thumbnails", out var thumbs))
				{
					if (thumbs.TryGetProperty("high", out var highThumb))
						avatarUrl = highThumb.GetProperty("url").GetString();
					else if (thumbs.TryGetProperty("default", out var defThumb))
						avatarUrl = defThumb.GetProperty("url").GetString();
				}

				ulong subscriberCount = 0;
				ulong videoCount = 0;
				if (channelItem.TryGetProperty("statistics", out var stats))
				{
					if (stats.TryGetProperty("subscriberCount", out var subProp))
						ulong.TryParse(subProp.GetString(), out subscriberCount);

					if (stats.TryGetProperty("videoCount", out var vidProp))
						ulong.TryParse(vidProp.GetString(), out videoCount);
				}

				return new YouTubeChannelInfoDto
				{
					ChannelId = channelId,
					Title = title,
					CustomUrl = customUrl,
					AvatarUrl = avatarUrl,
					SubscriberCount = subscriberCount,
					VideoCount = videoCount
				};
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[YouTube] Исключение при получении данных канала");
				return null;
			}
		}

		public async Task<(bool Success, string? VideoId, string? ErrorMessage)> UploadVideoAsync(
			byte[] videoBytes,
			string title,
			string description,
			List<string> tags,
			string accessToken)
		{
			try
			{
				// 1. Формируем метаданные видеоролика
				var metadata = new
				{
					snippet = new
					{
						title = title,
						description = description,
						tags = tags,
						categoryId = "22" // Категория по умолчанию: "People & Blogs"
					},
					status = new
					{
						privacyStatus = "public", // Сразу делаем публичным
						selfDeclaredMadeForKids = false
					}
				};

				var jsonMetadata = JsonSerializer.Serialize(metadata);
				var metadataContent = new StringContent(jsonMetadata, Encoding.UTF8, "application/json");

				// 2. ЭТАП 1: Инициализация сессии загрузки (Resumable Upload)
				var initUrl = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
				using var initReq = new HttpRequestMessage(HttpMethod.Post, initUrl)
				{
					Content = metadataContent
				};

				initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
				initReq.Headers.Add("X-Upload-Content-Length", videoBytes.Length.ToString());
				initReq.Headers.Add("X-Upload-Content-Type", "video/mp4");

				var initResp = await _httpClient.SendAsync(initReq);
				if (!initResp.IsSuccessStatusCode)
				{
					var err = await initResp.Content.ReadAsStringAsync();
					_logger.LogError("[YouTube Upload] Ошибка инициализации загрузки: {Err}", err);
					return (false, null, $"Init error: {err}");
				}

				// Google отдает URL для загрузки бинарника в заголовке Location
				var uploadUrl = initResp.Headers.Location;
				if (uploadUrl == null)
				{
					return (false, null, "Google не вернул заголовок Location для загрузки видео.");
				}

				// 3. ЭТАП 2: Загрузка самого видеофайла
				using var videoContent = new ByteArrayContent(videoBytes);
				videoContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");

				using var uploadReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
				{
					Content = videoContent
				};

				var uploadResp = await _httpClient.SendAsync(uploadReq);
				var responseJson = await uploadResp.Content.ReadAsStringAsync();

				if (!uploadResp.IsSuccessStatusCode)
				{
					_logger.LogError("[YouTube Upload] Ошибка загрузки видеопотока: {Err}", responseJson);
					return (false, null, $"Upload error: {responseJson}");
				}

				// Успешно! Достаем ID созданного видео
				using var doc = JsonDocument.Parse(responseJson);
				string videoId = doc.RootElement.GetProperty("id").GetString()!;

				_logger.LogInformation("✅ [YouTube Upload] Видео успешно опубликовано! VideoId: {Id}", videoId);
				return (true, videoId, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[YouTube Upload] Исключение при загрузке видео");
				return (false, null, ex.Message);
			}
		}
	}
}