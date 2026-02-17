using System.Text.Json;
using CrossChat.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("instagram")]
	public class InstagramController : ControllerBase
	{
		private readonly ILogger<InstagramController> _logger;
		private readonly SocialMediaSettings _settings;
		private readonly HttpClient _httpClient;

		// ИСПОЛЬЗУЕМ META APP ID (Ключ от "Умного дома", а не от "Сарая")
		private string InstagramAppId => _settings.InstagramAppId;
		private string InstagramAppSecret => _settings.InstagramAppSecret; // Секрет от Meta App 

		private string RedirectUri => $"{APP_URL}/instagram/auth/callback";

		private string AppId => _settings.AppId;
		private string AppSecret => _settings.AppSecret;

		private string faceBookRedirectUri = $"{APP_URL}/instagram/facebook/auth/callback";

		public InstagramController(ILogger<InstagramController> logger, IOptions<SocialMediaSettings> options)
		{
			_logger = logger;
			_settings = options.Value;
			_httpClient = new HttpClient();
		}

		[HttpGet]
		public IActionResult Index()
		{
			// ========================================================================
			// 1. НАСТРОЙКА INSTAGRAM BUSINESS LOGIN (Через Instagram API)
			// ========================================================================
			var instaScopes = string.Join(",",
				"instagram_business_basic",
				"instagram_business_manage_messages",
				"instagram_business_manage_comments",
				"instagram_business_content_publish",
				"instagram_business_manage_insights"
			);

			var instaLoginUrl = $"https://www.instagram.com/oauth/authorize?" +
						   $"client_id={InstagramAppId}&" +
						   $"redirect_uri={RedirectUri}&" + // Важно: URI должен быть добавлен в Instagram Login Settings
						   $"response_type=code&" +
						   $"force_reauth=true&" +
						   $"scope={instaScopes}";

			// ========================================================================
			// 2. НАСТРОЙКА FACEBOOK LOGIN (Через Facebook API)
			// ========================================================================
			var fbScopes = string.Join(",",
				"instagram_basic",
				"instagram_manage_messages",
				"instagram_manage_comments",
				"instagram_manage_insights",
				"pages_show_list",
				"pages_manage_metadata",
				"pages_read_engagement",
				"business_management"
			);

			var fbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?" +
							  $"client_id={AppId}&" + // Здесь ID Facebook приложения
							  $"redirect_uri={faceBookRedirectUri}&" +
							  $"response_type=code&" +
							  $"auth_type=reauthenticate&" +
							  $"scope={fbScopes}";

			// ========================================================================
			// 3. ГЕНЕРАЦИЯ HTML
			// ========================================================================
			var html = $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Вход в систему</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            display: flex;
            justify-content: center;
            align-items: center;
            min-height: 100vh;
            margin: 0;
            background: linear-gradient(135deg, #f0f2f5 0%, #eef2f3 100%);
        }}
        
        .card {{
            background: white;
            padding: 40px;
            border-radius: 20px;
            box-shadow: 0 10px 30px rgba(0,0,0,0.1);
            text-align: center;
            max-width: 400px;
            width: 90%;
        }}

        h1 {{ margin: 0 0 10px; font-size: 24px; color: #1c1e21; font-weight: 700; }}
        p {{ color: #65676b; font-size: 15px; margin-bottom: 30px; line-height: 1.5; }}

        /* ОБЩИЙ СТИЛЬ КНОПОК */
        .btn {{
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 12px;
            width: 100%;
            padding: 14px;
            border: none;
            border-radius: 12px;
            cursor: pointer;
            text-decoration: none;
            font-size: 16px;
            font-weight: 600;
            color: white;
            transition: transform 0.2s, box-shadow 0.2s, opacity 0.2s;
            margin-bottom: 15px; /* Отступ между кнопками */
            box-sizing: border-box;
        }}

        .btn:hover {{
            transform: translateY(-2px);
            opacity: 0.95;
        }}

        .btn svg {{
            width: 24px;
            height: 24px;
            fill: white;
        }}

        /* СТИЛЬ INSTAGRAM */
        .btn-insta {{
            background: linear-gradient(45deg, #f09433 0%, #e6683c 25%, #dc2743 50%, #cc2366 75%, #bc1888 100%);
            box-shadow: 0 4px 12px rgba(220, 39, 67, 0.2);
        }}
        .btn-insta:hover {{
            box-shadow: 0 8px 20px rgba(220, 39, 67, 0.35);
        }}

        /* СТИЛЬ FACEBOOK */
        .btn-fb {{
            background-color: #1877F2; /* Официальный цвет FB */
            box-shadow: 0 4px 12px rgba(24, 119, 242, 0.2);
        }}
        .btn-fb:hover {{
            box-shadow: 0 8px 20px rgba(24, 119, 242, 0.35);
        }}

        .separator {{
            display: flex;
            align-items: center;
            text-align: center;
            color: #ccc;
            margin: 20px 0;
            font-size: 13px;
        }}
        .separator::before, .separator::after {{
            content: '';
            flex: 1;
            border-bottom: 1px solid #e0e0e0;
        }}
        .separator::before {{ margin-right: 10px; }}
        .separator::after {{ margin-left: 10px; }}

    </style>
</head>
<body>
    <div class=""card"">
        <h1>Подключение каналов</h1>
        <p>Выберите способ авторизации, чтобы подключить чат-бота к вашим страницам.</p>
        
        <!-- КНОПКА INSTAGRAM -->
        <a href=""{instaLoginUrl}"" class=""btn btn-insta"">
            <svg viewBox=""0 0 24 24"">
                <path d=""M12 2.163c3.204 0 3.584.012 4.85.07 3.252.148 4.771 1.691 4.919 4.919.058 1.265.069 1.645.069 4.849 0 3.205-.012 3.584-.069 4.849-.149 3.225-1.664 4.771-4.919 4.919-1.266.058-1.644.07-4.85.07-3.204 0-3.584-.012-4.849-.07-3.26-.149-4.771-1.699-4.919-4.92-.058-1.265-.07-1.644-.07-4.849 0-3.204.013-3.583.07-4.849.149-3.227 1.664-4.771 4.919-4.919 1.266-.057 1.645-.069 4.849-.069zm0-2.163c-3.259 0-3.667.014-4.947.072-4.358.2-6.78 2.618-6.98 6.98-.059 1.281-.073 1.689-.073 4.948 0 3.259.014 3.668.072 4.948.2 4.358 2.618 6.78 6.98 6.98 1.281.058 1.689.072 4.948.072 3.259 0 3.668-.014 4.948-.072 4.354-.2 6.782-2.618 6.979-6.98.059-1.28.073-1.689.073-4.948 0-3.259-.014-3.667-.072-4.947-.196-4.354-2.617-6.78-6.979-6.98-1.281-.059-1.69-.073-4.949-.073zm0 5.838c-3.403 0-6.162 2.759-6.162 6.162s2.759 6.163 6.162 6.163 6.162-2.759 6.162-6.163c0-3.403-2.759-6.162-6.162-6.162zm0 10.162c-2.209 0-4-1.79-4-4 0-2.209 1.791-4 4-4s4 1.791 4 4c0 2.21-1.791 4-4 4zm6.406-11.845c-.796 0-1.441.645-1.441 1.44s.645 1.44 1.441 1.44c.795 0 1.439-.645 1.439-1.44s-.644-1.44-1.439-1.44z""/>
            </svg>
            <span>Войти через Instagram</span>
        </a>

        <div class=""separator"">ИЛИ</div>

        <!-- КНОПКА FACEBOOK -->
        <a href=""{fbLoginUrl}"" class=""btn btn-fb"">
            <!-- Официальная иконка Facebook (F) -->
            <svg viewBox=""0 0 24 24"">
                <path d=""M24 12.073c0-6.627-5.373-12-12-12s-12 5.373-12 12c0 5.99 4.388 10.954 10.125 11.854v-8.385H7.078v-3.47h3.047V9.43c0-3.007 1.792-4.669 4.533-4.669 1.312 0 2.686.235 2.686.235v2.953H15.83c-1.491 0-1.956.925-1.956 1.874v2.25h3.328l-.532 3.47h-2.796v8.385C19.612 23.027 24 18.062 24 12.073z""/>
            </svg>
            <span>Войти через Facebook</span>
        </a>
    </div>
</body>
</html>";

			return Content(html, "text/html");
		}

		[HttpGet("auth/callback")]
		public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
		{
			// -----------------------------------------------------------------------
			// 1. ОБРАБОТКА ОШИБОК (Отказ пользователя)
			// -----------------------------------------------------------------------
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				return ReturnHtmlPage(false,
					"Ошибка авторизации",
					"Вы отменили вход или произошла ошибка на стороне Instagram.",
					$"Error: {error}<br/>Details: {error_description}",
					"");
			}

			try
			{
				var cleanCode = code.Replace("#_", "");

				// -----------------------------------------------------------------------
				// 2. STEP 2: Обмен кода на Short-Lived Token
				// -----------------------------------------------------------------------
				var shortTokenUrl = "https://api.instagram.com/oauth/access_token";
				var formData = new Dictionary<string, string>
		{
			{ "client_id", InstagramAppId },
			{ "client_secret", InstagramAppSecret },
			{ "grant_type", "authorization_code" },
			{ "redirect_uri", RedirectUri },
			{ "code", cleanCode }
		};

				var shortResponse = await _httpClient.PostAsync(shortTokenUrl, new FormUrlEncodedContent(formData));
				var shortJsonStr = await shortResponse.Content.ReadAsStringAsync();

				if (!shortResponse.IsSuccessStatusCode)
				{
					return ReturnHtmlPage(false, "Ошибка Step 2", "Не удалось получить короткий токен.", shortJsonStr, "");
				}

				using var shortDoc = JsonDocument.Parse(shortJsonStr);
				var shortRoot = shortDoc.RootElement;

				if (!shortRoot.TryGetProperty("access_token", out JsonElement shortTokenEl))
					return ReturnHtmlPage(false, "Ошибка JSON", "В ответе нет access_token.", shortJsonStr, "");

				var shortAccessToken = shortTokenEl.GetString();

				// -----------------------------------------------------------------------
				// 3. STEP 3: Обмен на Long-Lived Token (60 дней)
				// -----------------------------------------------------------------------
				var longTokenUrl = $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={InstagramAppSecret}&access_token={shortAccessToken}";
				var longResponse = await _httpClient.GetAsync(longTokenUrl);
				var longJsonStr = await longResponse.Content.ReadAsStringAsync();

				if (!longResponse.IsSuccessStatusCode)
				{
					return ReturnHtmlPage(false, "Ошибка Step 3", "Не удалось получить длинный токен.", longJsonStr, "");
				}

				using var longDoc = JsonDocument.Parse(longJsonStr);
				var longRoot = longDoc.RootElement;
				var longAccessToken = longRoot.GetProperty("access_token").GetString();
				var expiresInSeconds = longRoot.GetProperty("expires_in").GetInt32();
				var expireDate = DateTime.UtcNow.AddSeconds(expiresInSeconds);

				// -----------------------------------------------------------------------
				// 4. STEP 4: Получаем имя пользователя (для проверки)
				// -----------------------------------------------------------------------
				var userUrl = $"https://graph.instagram.com/me?fields=id,username&access_token={longAccessToken}";
				var userResponse = await _httpClient.GetAsync(userUrl);
				var userJsonStr = await userResponse.Content.ReadAsStringAsync();

				var username = "Unknown";
				var userId = "Unknown";

				if (userResponse.IsSuccessStatusCode)
				{
					var userDoc = JsonDocument.Parse(userJsonStr);
					if (userDoc.RootElement.TryGetProperty("username", out var u)) username = u.GetString();
					if (userDoc.RootElement.TryGetProperty("id", out var i)) userId = i.GetString();
				}

				// -----------------------------------------------------------------------
				// 5. ГЕНЕРАЦИЯ HTML С КНОПКОЙ КОПИРОВАНИЯ
				// -----------------------------------------------------------------------
				var infoHtml = $@"
            <div class='info-grid'>
                <div><strong>Username:</strong> @{username}</div>
                <div><strong>User ID:</strong> {userId}</div>
                <div><strong>Истекает:</strong> {expireDate:dd.MM.yyyy HH:mm} (UTC)</div>
            </div>
            
            <div class='token-section'>
                <p>Ваш Long-Lived Access Token (60 дней):</p>
                <textarea id='tokenArea' readonly onclick='this.select()'>{longAccessToken}</textarea>
                <button onclick='copyToken()' id='copyBtn'>📋 Скопировать токен</button>
                <span id='copyMsg' style='display:none; color: green; margin-left: 10px; font-weight:bold;'>Скопировано!</span>
            </div>";

				return ReturnHtmlPage(true, "Успешно!", "Instagram Business подключен.", infoHtml, longAccessToken);
			}
			catch (Exception ex)
			{
				return ReturnHtmlPage(false, "System Error", ex.Message, ex.StackTrace, "");
			}
		}

		/// <summary>
		/// Эндпоинт для деавторизации (Instagram вызывает при отзыве доступа)
		/// </summary>
		[HttpGet("deauth")]
		[HttpPost("deauth")]
		public IActionResult DeauthorizationCallback(
			[FromQuery] string signed_request = null,
			[FromQuery] string code = null)  // code - это код подтверждения удаления
		{
			_logger.LogInformation($"=== Deauthorization callback received ===");
			_logger.LogInformation($"Signed request: {signed_request}");
			_logger.LogInformation($"Code: {code}");

			try
			{
				if (!string.IsNullOrEmpty(signed_request))
				{
					// 1. Разбираем signed_request от Facebook
					var parts = signed_request.Split('.');
					if (parts.Length == 2)
					{
						// payload - это base64url закодированный JSON
						var payload = parts[1];
						// Добавляем padding если нужно
						payload = payload.Replace('-', '+').Replace('_', '/');
						switch (payload.Length % 4)
						{
							case 2: payload += "=="; break;
							case 3: payload += "="; break;
						}

						var payloadBytes = Convert.FromBase64String(payload);
						var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);

						_logger.LogInformation($"Decoded payload: {payloadJson}");

						// Здесь можно найти user_id того, кто отозвал доступ
						var data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
						var userId = data.user_id?.ToString();
						var algorithm = data.algorithm?.ToString();

						_logger.LogInformation($"User {userId} deauthorized app. Algorithm: {algorithm}");

						// TODO: Удалите данные пользователя из вашей БД
						// await _userService.DeleteUserByFacebookId(userId);
					}
				}
				else if (!string.IsNullOrEmpty(code))
				{
					// Instagram может передать code для подтверждения удаления
					_logger.LogInformation($"Deauthorization code: {code}");
				}

				// Всегда возвращаем 200 OK, иначе Instagram будет повторять запрос
				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing deauthorization");
				// Всё равно возвращаем 200, чтобы Instagram не зациклился
				return Ok();
			}
		}

		/// <summary>
		/// Эндпоинт для удаления данных пользователя (Data Deletion Request)
		/// </summary>
		[HttpGet("data-deletion")]
		[HttpPost("data-deletion")]
		public IActionResult DataDeletionCallback(
			[FromQuery] string signed_request = null,
			[FromQuery] string code = null,
			[FromQuery] string confirmation_code = null)
		{
			_logger.LogInformation($"=== Data Deletion callback received ===");
			_logger.LogInformation($"Signed request: {signed_request}");
			_logger.LogInformation($"Code: {code}");
			_logger.LogInformation($"Confirmation code: {confirmation_code}");

			try
			{
				string userId = null;
				string confirmationCode = confirmation_code ?? code ?? Guid.NewGuid().ToString("N");

				if (!string.IsNullOrEmpty(signed_request))
				{
					// Разбираем signed_request
					var parts = signed_request.Split('.');
					if (parts.Length == 2)
					{
						var payload = parts[1]
							.Replace('-', '+')
							.Replace('_', '/');

						switch (payload.Length % 4)
						{
							case 2: payload += "=="; break;
							case 3: payload += "="; break;
						}

						var payloadBytes = Convert.FromBase64String(payload);
						var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);

						_logger.LogInformation($"Decoded payload: {payloadJson}");

						var data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
						userId = data.user_id?.ToString();
					}
				}

				if (string.IsNullOrEmpty(userId))
				{
					userId = $"unknown_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
				}

				_logger.LogInformation($"Processing data deletion for user: {userId}");

				// TODO: Реально удалите все данные пользователя из вашей БД
				// await _userService.DeleteAllUserData(userId);

				// Генерируем уникальный номер запроса на удаление
				var deletionRequestId = Guid.NewGuid().ToString("N");
				var statusUrl = $"{APP_URL}/instagram/deletion-status/{deletionRequestId}";

				// Instagram ожидает JSON ответ в определенном формате
				var response = new
				{
					url = statusUrl,  // URL где пользователь может проверить статус удаления
					confirmation_code = confirmationCode,
					user_id = userId,
					status = "success", // или "processing"
					estimated_completion_time = 3600 // в секундах (1 час)
				};

				_logger.LogInformation($"Data deletion response: {JsonConvert.SerializeObject(response)}");

				return Ok(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing data deletion");

				// Даже при ошибке возвращаем корректный JSON
				return Ok(new
				{
					url = $"{APP_URL}/instagram/deletion-status/error-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
					confirmation_code = Guid.NewGuid().ToString("N"),
					status = "error",
					error_description = "Internal server error. Our team has been notified."
				});
			}
		}

		[HttpGet("facebook/auth/callback")]
		public async Task<IActionResult> FaceBookOAuthCallback(
			[FromQuery] string code,
			[FromQuery] string state = null,
			[FromQuery] string error = null,
			[FromQuery] string error_reason = null,
			[FromQuery] string error_description = null)
		{
			// -----------------------------------------------------------------------
			// 1. ОБРАБОТКА ОШИБОК ОТ FACEBOOK
			// -----------------------------------------------------------------------
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				return ReturnHtmlPage(false,
					"Ошибка авторизации Facebook",
					"Пользователь отменил вход или возникла ошибка API.",
					$"Error: {error}<br/>Reason: {error_reason}<br/>Description: {error_description}",
					"");
			}

			try
			{
				_logger.LogInformation("=== Facebook Callback Started ===");

				// -----------------------------------------------------------------------
				// 2. ПАРСИНГ STATE (Получаем ID пользователя из вашей системы)
				// -----------------------------------------------------------------------
				string internalUserId = "unknown";
				if (!string.IsNullOrEmpty(state))
				{
					try
					{
						// Иногда state приходит дважды закодированным или с экранированием
						var cleanState = state.Replace("\\\"", "\"");
						// Если у вас свой класс InstagramAuthState, используйте его
						dynamic stateData = JsonConvert.DeserializeObject(cleanState);
						internalUserId = stateData?.UserId ?? "unknown";
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "Ошибка парсинга State");
					}
				}

				// -----------------------------------------------------------------------
				// 3. STEP A: Получение Short-Lived User Token (Живет 1-2 часа)
				// -----------------------------------------------------------------------
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={faceBookRedirectUri}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				var shortResponse = await _httpClient.GetAsync(shortTokenUrl);
				var shortJsonStr = await shortResponse.Content.ReadAsStringAsync();

				if (!shortResponse.IsSuccessStatusCode)
				{
					return ReturnHtmlPage(false, "Ошибка Step A (Short Token)",
						"Не удалось обменять код на токен.", shortJsonStr, "");
				}

				using var shortDoc = JsonDocument.Parse(shortJsonStr);
				var shortAccessToken = shortDoc.RootElement.GetProperty("access_token").GetString();

				// -----------------------------------------------------------------------
				// 4. STEP B: Обмен на Long-Lived User Token (Живет 60 дней)
				// -----------------------------------------------------------------------
				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortAccessToken}";

				var longResponse = await _httpClient.GetAsync(longTokenUrl);
				var longJsonStr = await longResponse.Content.ReadAsStringAsync();

				if (!longResponse.IsSuccessStatusCode)
				{
					// Если не удалось получить длинный, показываем ошибку, но выводим короткий, который успели получить
					return ReturnHtmlPage(false, "Ошибка Step B (Long Token)",
						"Короткий токен получен, но обмен на длинный не удался.",
						$"Short Token: {shortAccessToken}<br/><br/>Error: {longJsonStr}", "");
				}

				using var longDoc = JsonDocument.Parse(longJsonStr);
				var longRoot = longDoc.RootElement;
				var longAccessToken = longRoot.GetProperty("access_token").GetString();

				// Facebook иногда возвращает expires_in, иногда нет (для вечных токенов). 
				// Если нет - ставим 60 дней по умолчанию.
				var expiresInSeconds = longRoot.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 5184000;
				var expireDate = DateTime.UtcNow.AddSeconds(expiresInSeconds);

				// -----------------------------------------------------------------------
				// 5. STEP C: Получение данных профиля (Имя и ID)
				// -----------------------------------------------------------------------
				var meUrl = $"https://graph.facebook.com/me?fields=id,name,email&access_token={longAccessToken}";
				var meResponse = await _httpClient.GetAsync(meUrl);
				var meJsonStr = await meResponse.Content.ReadAsStringAsync();

				var fbName = "Unknown";
				var fbId = "Unknown";

				if (meResponse.IsSuccessStatusCode)
				{
					using var meDoc = JsonDocument.Parse(meJsonStr);
					if (meDoc.RootElement.TryGetProperty("name", out var n)) fbName = n.GetString();
					if (meDoc.RootElement.TryGetProperty("id", out var i)) fbId = i.GetString();
				}

				// -----------------------------------------------------------------------
				// 6. ГЕНЕРАЦИЯ HTML
				// -----------------------------------------------------------------------
				var infoHtml = $@"
					<div class='info-grid'>
						<div><strong>FB Name:</strong> {fbName}</div>
						<div><strong>FB User ID:</strong> {fbId}</div>
						<div><strong>Internal ID:</strong> {internalUserId}</div>
						<div><strong>Истекает:</strong> {expireDate:dd.MM.yyyy HH:mm} (UTC)</div>
					</div>
            
					<div class='token-section'>
						<p>Ваш Long-Lived Facebook Token (60 дней):</p>
						<textarea id='tokenArea' readonly onclick='this.select()'>{longAccessToken}</textarea>
						<button onclick='copyToken()' id='copyBtn'>📋 Скопировать токен</button>
						<span id='copyMsg' style='display:none; color: green; margin-left: 10px; font-weight:bold;'>Скопировано!</span>
					</div>

					<div style='margin-top: 20px;'>
						 <details>
							<summary style='cursor:pointer; color:#555;'>Показать Short-Lived Token (Техническая инфо)</summary>
							<div class='raw-error' style='background:#f1f1f1; color:#333; border:none;'>
								{shortAccessToken}
							</div>
						 </details>
					</div>";

				return ReturnHtmlPage(true, "Facebook Подключен!", "Авторизация прошла успешно.", infoHtml, longAccessToken);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Critical Error in Facebook Callback");
				return ReturnHtmlPage(false, "System Error", ex.Message, ex.StackTrace, "");
			}
		}

		// ---------------------------------------------------------------------------
		// ВСПОМОГАТЕЛЬНЫЙ МЕТОД ГЕНЕРАЦИИ HTML
		// ---------------------------------------------------------------------------
		private ContentResult ReturnHtmlPage(bool isSuccess, string title, string message, string htmlContent, string rawToken)
		{
			var color = isSuccess ? "#2ecc71" : "#e74c3c"; // Зеленый / Красный
			var icon = isSuccess ? "check_circle" : "error";

			var page = $@"
    <!DOCTYPE html>
    <html lang='ru'>
    <head>
        <meta charset='utf-8'>
        <title>{title}</title>
        <link href='https://fonts.googleapis.com/icon?family=Material+Icons' rel='stylesheet'>
        <style>
            body {{ font-family: 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f0f2f5; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; }}
            .card {{ background: white; padding: 40px; border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.1); width: 100%; max-width: 700px; text-align: center; }}
            .icon {{ font-size: 64px; color: {color}; margin-bottom: 20px; }}
            h1 {{ color: #2c3e50; margin: 0 0 10px 0; }}
            p.subtitle {{ color: #7f8c8d; font-size: 18px; margin-bottom: 30px; }}
            
            .info-grid {{ display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; background: #f8f9fa; padding: 15px; border-radius: 8px; margin-bottom: 20px; text-align: left; font-size: 14px; border: 1px solid #e9ecef; }}
            
            .token-section {{ text-align: left; margin-top: 20px; }}
            .token-section p {{ font-weight: bold; color: #34495e; margin-bottom: 5px; }}
            
            textarea {{ width: 100%; height: 100px; padding: 10px; border: 2px solid #dfe6e9; border-radius: 8px; font-family: 'Courier New', monospace; font-size: 13px; color: #2d3436; resize: none; background: #fafafa; box-sizing: border-box; }}
            textarea:focus {{ outline: none; border-color: {color}; }}
            
            button {{ background-color: {color}; color: white; border: none; padding: 12px 25px; font-size: 16px; border-radius: 8px; cursor: pointer; margin-top: 10px; transition: background 0.3s; display: inline-flex; align-items: center; justify-content: center; }}
            button:hover {{ opacity: 0.9; }}
            
            .raw-error {{ background: #fff0f0; color: #c0392b; padding: 15px; border-radius: 5px; text-align: left; font-family: monospace; font-size: 12px; overflow-x: auto; margin-top: 20px; border: 1px solid #ffcccc; }}
        </style>
        <script>
            function copyToken() {{
                var copyText = document.getElementById('tokenArea');
                copyText.select();
                copyText.setSelectionRange(0, 99999); /* For mobile devices */
                navigator.clipboard.writeText(copyText.value).then(function() {{
                    document.getElementById('copyMsg').style.display = 'inline';
                    setTimeout(function() {{ document.getElementById('copyMsg').style.display = 'none'; }}, 3000);
                }}, function(err) {{
                    alert('Не удалось скопировать автоматически. Пожалуйста, скопируйте вручную.');
                }});
            }}
        </script>
    </head>
    <body>
        <div class='card'>
            <span class='material-icons icon'>{icon}</span>
            <h1>{title}</h1>
            <p class='subtitle'>{message}</p>
            
            {htmlContent}
        </div>
    </body>
    </html>";

			return Content(page, "text/html");
		}
	}
}