using System.Security.Cryptography;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("instagram")]
	public class InstagramController : ControllerBase
	{
		private readonly ILogger<InstagramController> _logger;
		private const string INSTAGRAM_OAUTH_URL = "https://www.instagram.com/consent/?flow=ig_biz_login_oauth";
		private const string INSTAGRAM_TOKEN_URL = "https://api.instagram.com/oauth/access_token";
		private const string INSTAGRAM_GRAPH_URL = "https://graph.instagram.com";
		private const string INSTAGRAM_API_AUTH = "https://api.instagram.com/oauth/authorize";

		private string AppId => "1660493108654598";
		private string AppSecret => "bdf384bf7e6388dd00d811dc47b3d94c";

		private const string BaseUrl = "https://crosschat-fabc.onrender.com";

		private const string redirectUri = $"{BaseUrl}/instagram/auth/callback";

		public InstagramController(ILogger<InstagramController> logger)
		{
			_logger = logger;
		}

		[HttpGet]
		public ContentResult Index()
		{
			string html = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Вход через Instagram</title>
    <style>
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        }
        
        .container {
            text-align: center;
            background: white;
            padding: 40px;
            border-radius: 20px;
            box-shadow: 0 20px 40px rgba(0,0,0,0.1);
            max-width: 400px;
            width: 90%;
        }
        
        h1 {
            color: #333;
            margin-bottom: 20px;
            font-size: 24px;
        }
        
        p {
            color: #666;
            margin-bottom: 30px;
            line-height: 1.5;
        }
        
        .instagram-btn {
            background: linear-gradient(45deg, #f09433 0%, #e6683c 25%, #dc2743 50%, #cc2366 75%, #bc1888 100%);
            color: white;
            border: none;
            padding: 12px 30px;
            font-size: 18px;
            border-radius: 50px;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            transition: transform 0.2s, box-shadow 0.2s;
            border: 1px solid rgba(255,255,255,0.3);
            font-weight: 600;
        }
        
        .instagram-btn:hover {
            transform: scale(1.05);
            box-shadow: 0 10px 20px rgba(0,0,0,0.2);
        }
        
        .instagram-btn img {
            width: 24px;
            height: 24px;
            filter: brightness(0) invert(1);
        }
        
        .footer {
            margin-top: 30px;
            font-size: 12px;
            color: #999;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <h1>Добро пожаловать!</h1>
        <p>Подключите Instagram чтобы продолжить</p>
        
        <!-- КНОПКА ВХОДА ЧЕРЕЗ INSTAGRAM -->
        <button onclick=""window.location.href='https://api.instagram.com/oauth/authorize?client_id=1660493108654598&redirect_uri=https://crosschat-fabc.onrender.com/instagram/auth/callback&scope=user_profile,user_media&response_type=code'"" 
                class=""instagram-btn"">
            <span>Войти через Instagram</span>
        </button>
        
        <div class=""footer"">
            Нажимая кнопку, вы соглашаетесь с условиями использования
        </div>
    </div>
</body>
</html>";
			return Content(html, "text/html");
		}


		// Эндпоинт 1: Получение OAuth ссылки
		[HttpGet("auth/url")]
		public IActionResult GetAuthUrl([FromQuery] string userId)
		{
			try
			{
				if (string.IsNullOrEmpty(userId))
				{
					return BadRequest(new { error = "User ID is required" });
				}

				// Генерируем ссылку
				var authUrl = GenerateSimpleOAuthUrl(userId);

				_logger.LogInformation($"Auth URL generated for user {userId}");

				return Ok(new { auth_url = authUrl });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error generating auth URL");
				return StatusCode(500, new { error = "Internal server error" });
			}
		}

		// Эндпоинт 2: Callback от Instagram (OAuth redirect)
		[HttpGet("auth/callback")]
		public async Task<IActionResult> OAuthCallback(
			[FromQuery] string code,
			[FromQuery] string state = null,
			[FromQuery] string error = null,
			[FromQuery] string error_reason = null,
			[FromQuery] string error_description = null)
		{
			try
			{
				_logger.LogInformation($"=== Callback received ===");
				_logger.LogInformation($"Raw code: {code?.Substring(0, Math.Min(20, code?.Length ?? 0))}...");
				_logger.LogInformation($"Raw state: {state}");

				string userId = "unknown";

				if (!string.IsNullOrEmpty(state))
				{
					try
					{
						// Декодируем URL-кодирование
						var decodedState = state;
						_logger.LogInformation($"Decoded state: {decodedState}");

						// Убираем экранирование кавычек
						var unescapedState = decodedState.Replace("\\\"", "\"");
						_logger.LogInformation($"Unescaped state: {unescapedState}");

						var stateData = JsonConvert.DeserializeObject<InstagramAuthState>(unescapedState);
						if (stateData != null)
						{
							userId = stateData.UserId ?? "unknown";
							_logger.LogInformation($"Extracted user_id: {userId}");
						}
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Failed to parse state");
					}
				}

				var tokenResponse = await ExchangeCodeForTokenAsync(code, redirectUri);

				return Ok(new
				{
					success = true,
					message = "Instagram успешно подключен!",
					user_id = userId,
					instagram_user_id = tokenResponse.UserId.ToString(),
					access_token = tokenResponse?.AccessToken?.Substring(0, Math.Min(20, tokenResponse?.AccessToken?.Length ?? 0)) + "...",
					expires_in = tokenResponse?.ExpiresIn
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in OAuth callback");
				return StatusCode(500, new { error = ex.Message });
			}
		}

		// Генерация CSRF токена
		public string GenerateCsrfToken()
		{
			using var rng = RandomNumberGenerator.Create();
			byte[] tokenData = new byte[32];
			rng.GetBytes(tokenData);
			return Convert.ToBase64String(tokenData).Replace("+", "-").Replace("/", "_").Replace("=", "");
		}

		// Альтернативная упрощенная версия
		public string GenerateSimpleOAuthUrl(string userId)
		{
			var csrfToken = GenerateCsrfToken();
			var state = new InstagramAuthState
			{
				UserId = userId,
				Provider = "instagram",
				Token = csrfToken,
				CallbackUrl = $"{BaseUrl}/instagram/auth/callback"
			};

			var stateJson = JsonConvert.SerializeObject(state);
			var encodedState = HttpUtility.UrlEncode(stateJson);

			return $"{INSTAGRAM_API_AUTH}?" +
				   $"client_id={AppId}&" +
				   $"redirect_uri={HttpUtility.UrlEncode($"{BaseUrl}/instagram/auth/callback")}&" +
				   $"scope=instagram_business_basic,instagram_business_manage_messages,instagram_business_manage_comments, instagram_business_content_publish&" +
				   $"response_type=code&" +
				   $"state={encodedState}";
		}

		// Обмен кода на токен
		public async Task<InstagramTokenResponse> ExchangeCodeForTokenAsync(string code, string redirectUri)
		{
			try
			{
				_logger.LogInformation($"=== Starting token exchange ===");
				_logger.LogInformation($"AppId: {AppId}");
				_logger.LogInformation($"AppSecret length: {AppSecret?.Length}");
				_logger.LogInformation($"RedirectUri: {redirectUri}");
				_logger.LogInformation($"Code length: {code?.Length}");
				_logger.LogInformation($"Code preview: {code?.Substring(0, Math.Min(20, code?.Length ?? 0))}...");

				using var client = new HttpClient();

				// ШАГ 1: Обмен кода на КРАТКОВРЕМЕННЫЙ токен (1 час)
				var shortLivedRequest = new HttpRequestMessage(HttpMethod.Post,
					"https://api.instagram.com/oauth/access_token");

				var formData = new Dictionary<string, string>
				{
					["client_id"] = AppId,
					["client_secret"] = AppSecret,
					["grant_type"] = "authorization_code",
					["redirect_uri"] = redirectUri,
					["code"] = code
				};

				shortLivedRequest.Content = new FormUrlEncodedContent(formData);

				var shortLivedResponse = await client.SendAsync(shortLivedRequest);
				var shortLivedJson = await shortLivedResponse.Content.ReadAsStringAsync();

				_logger.LogInformation("Короткий токен" + shortLivedJson);

				var shortLivedToken = JsonConvert.DeserializeObject<InstagramShortLivedToken>(shortLivedJson);

				// Логируем получение кратковременного токена
				_logger.LogInformation("Short-lived token obtained. User ID: {UserId}",
					shortLivedToken.UserId);

				// ШАГ 2: Обмен КРАТКОВРЕМЕННОГО на ДОЛГОВРЕМЕННЫЙ токен (60 дней)
				var longLivedUrl = $"https://graph.instagram.com/access_token" +
					$"?grant_type=ig_exchange_token" +
					$"&client_secret={Uri.EscapeDataString(AppSecret)}" +
					$"&access_token={Uri.EscapeDataString(shortLivedToken.AccessToken)}";

				var longLivedResponse = await client.GetAsync(longLivedUrl);
				var longLivedJson = await longLivedResponse.Content.ReadAsStringAsync();

				_logger.LogInformation($"Long-lived RAW JSON: {longLivedJson}");

				if (!longLivedResponse.IsSuccessStatusCode)
				{
					_logger.LogError($"Long-lived token error: {longLivedResponse.StatusCode}, Body: {longLivedJson}");
					throw new Exception($"Long-lived token error: {longLivedJson}");
				}

				var longObj = JObject.Parse(longLivedJson);
				var longLivedToken = new InstagramLongLivedToken
				{
					AccessToken = longObj["access_token"]?.ToString(),
					TokenType = longObj["token_type"]?.ToString() ?? "bearer",
					ExpiresIn = longObj["expires_in"]?.Value<int>() ?? 0
				};

				// ПРОВЕРКА: если токен null - показываем всю структуру JSON
				if (string.IsNullOrEmpty(longLivedToken.AccessToken))
				{
					_logger.LogError($"LONG-LIVED TOKEN IS NULL! Full JSON: {longLivedJson}");
					_logger.LogError($"Available fields: {string.Join(", ", longObj.Properties().Select(p => p.Name))}");

					// Может быть под другим именем?
					var possibleTokens = new[] { "access_token", "access-token", "token", "data.access_token" };
					foreach (var field in possibleTokens)
					{
						var token = longObj.SelectToken(field)?.ToString();
						if (!string.IsNullOrEmpty(token))
						{
							_logger.LogInformation($"Found token at field '{field}': {token.Substring(0, 15)}...");
							longLivedToken.AccessToken = token;
							break;
						}
					}
				}

				var expiresAt = DateTime.UtcNow.AddSeconds(longLivedToken.ExpiresIn);

				// ТЕПЕРЬ ЛОГИРУЕМ ТОКЕН (обрезанный для безопасности)
				_logger.LogInformation($"Long-lived token obtained. Expires at: {expiresAt}");
				if (!string.IsNullOrEmpty(longLivedToken.AccessToken))
				{
					_logger.LogInformation($"Long-lived token preview: {longLivedToken.AccessToken.Substring(0, 15)}...{longLivedToken.AccessToken.Substring(longLivedToken.AccessToken.Length - 10)}");
					_logger.LogInformation($"Long-lived token length: {longLivedToken.AccessToken.Length}");
				}
				else
				{
					_logger.LogError($"LONG-LIVED TOKEN IS STILL NULL AFTER PARSING!");
				}

				return new InstagramTokenResponse
				{
					AccessToken = longLivedToken.AccessToken,
					UserId = shortLivedToken.UserId
					//ExpiresAt = expiresAt,
					//Permissions = shortLivedToken.Permissions?.Split(',').ToList()
				};
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error exchanging code for token");
				throw;
			}
		}
	}

	#region Models
	public class InstagramShortLivedToken
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("user_id")]
		public long UserId { get; set; }

		[JsonProperty("permissions")]
		public List<string> Permissions { get; set; }

		[JsonIgnore]
		public string RawResponse { get; set; }
	}

	public class InstagramLongLivedToken
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("token_type")]
		public string TokenType { get; set; }

		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }

		[JsonIgnore]
		public string RawResponse { get; set; }
	}

	// Models/InstagramAuthModels.cs
	public class InstagramAuthState
	{
		[JsonProperty("user_id")]
		public string UserId { get; set; }

		[JsonProperty("provider")]
		public string Provider { get; set; }

		[JsonProperty("token")]
		public string Token { get; set; }

		[JsonProperty("callback_url")]
		public string CallbackUrl { get; set; }

		[JsonProperty("messages_callback")]
		public string MessagesCallback { get; set; }
	}

	public class InstagramTokenResponse
	{
		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("user_id")]
		public long UserId { get; set; }

		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }
	}

	#endregion
}
