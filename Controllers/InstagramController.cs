using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("instagram")]
	public class InstagramController : Controller // Наследуем от Controller, чтобы работали View и Redirect
	{
		private readonly ILogger<InstagramController> _logger;
		private readonly SocialMediaSettings _settings;
		private readonly HttpClient _httpClient;
		private readonly AppDbContext _db;

		private const string GraphApiVersion = "v21.0";
		private string InstagramAppId => _settings.InstagramAppId;
		private string InstagramAppSecret => _settings.InstagramAppSecret;
		private string AppId => _settings.AppId;
		private string AppSecret => _settings.AppSecret;

		private string RedirectUri => $"{APP_URL}/instagram/auth/callback";
		private string FaceBookRedirectUri => $"{APP_URL}/instagram/facebook/auth/callback";

		public InstagramController(
			ILogger<InstagramController> logger,
			IOptions<SocialMediaSettings> options,
			AppDbContext db)
		{
			_logger = logger;
			_settings = options.Value;
			_db = db;
			_httpClient = new HttpClient();
		}

		// ==========================================================
		// 1. ГЛАВНАЯ СТРАНИЦА НАСТРОЕК (/instagram)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index()
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Загружаем настройки, чтобы передать их во View
			var user = await _db.Users
				.Include(u => u.InstagramSettings)
				.FirstOrDefaultAsync(u => u.Id == userId);

			// Генерируем ссылки для кнопок (они нужны, если user.InstagramSettings == null)
			var instaScopes = string.Join(",",
				"instagram_business_basic",
				"instagram_business_manage_messages",
				"instagram_business_manage_comments",
				"instagram_business_content_publish",
				"instagram_business_manage_insights"
			);
			ViewBag.InstaLoginUrl = $"https://www.instagram.com/oauth/authorize?" +
						   $"client_id={InstagramAppId}&" +
						   $"redirect_uri={RedirectUri}&" + // Важно: URI должен быть добавлен в Instagram Login Settings
						   $"response_type=code&" +
						   $"force_reauth=true&" +
						   $"scope={instaScopes}";

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
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?" +
							  $"client_id={AppId}&" + // Здесь ID Facebook приложения
							  $"redirect_uri={FaceBookRedirectUri}&" +
							  $"response_type=code&" +
							  $"auth_type=reauthenticate&" +
							  $"scope={fbScopes}";

			// Передаем модель настроек (может быть null)
			return View(user?.InstagramSettings);
		}

		// ==========================================================
		// 3. ОТКЛЮЧЕНИЕ АККАУНТА (ПОЛЬЗОВАТЕЛЕМ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.InstagramSettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings != null && !string.IsNullOrEmpty(settings.AccessToken))
			{
				// Сначала пытаемся честно отписаться от вебхуков
				try
				{
					await ManageWebhooksAsync(settings.AccessToken, false);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Could not unsubscribe before disconnect. proceeding anyway.");
				}

				// Стираем данные
				settings.AccessToken = null;
				settings.InstagramBusinessId = null;
				settings.Username = null;
				settings.ProfilePictureUrl = null;
				settings.IsActive = false; // Важно сбросить

				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index");
		}

		// ==========================================================
		// 2. ОБНОВЛЕНИЕ НАСТРОЕК (ПРОМПТ / ВКЛЮЧЕНИЕ)
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(string systemPrompt, bool isActive)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.InstagramSettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				// Если статус меняется (был выкл -> стал вкл, или наоборот)
				if (settings.IsActive != isActive)
				{
					// Вызываем метод управления вебхуками
					bool success = await ManageWebhooksAsync(settings.AccessToken, isActive);

					if (!success)
					{
						// Если API Инстаграма вернуло ошибку, не меняем статус в БД
						// Можно добавить TempData["Error"] = "Не удалось обновить подписку";
						_logger.LogError($"Failed to change subscription state to {isActive} for user {userId}");
					}
					else
					{
						settings.IsActive = isActive;
					}
				}

				// Промпт обновляем в любом случае
				settings.SystemPrompt = systemPrompt;

				await _db.SaveChangesAsync();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error updating settings");
			}

			return RedirectToAction("Index");
		}

		// ==========================================================
		// ВСПОМОГАТЕЛЬНЫЙ МЕТОД: ПОДПИСКА НА ВЕБХУКИ
		// ==========================================================
		private async Task<bool> ManageWebhooksAsync(string accessToken, bool subscribe)
		{
			var url = $"https://graph.instagram.com/{GraphApiVersion}/me/subscribed_apps?access_token={accessToken}";

			HttpResponseMessage response;

			if (subscribe)
			{
				// === ПОДПИСКА (POST) ===
				var payload = new
				{
					subscribed_fields = new[]
					{
						"messages",
						"messaging_postbacks",
						"messaging_seen",
						"messaging_handover",
						"messaging_referral",
						"message_reactions",
						"standby",
						"comments",
						"live_comments",
						"mentions",
						"story_insights"
					}
				};

				var json = System.Text.Json.JsonSerializer.Serialize(payload);
				response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
				_logger.LogInformation("Subscribing to Webhooks...");
			}
			else
			{
				// === ОТПИСКА (DELETE) ===
				response = await _httpClient.DeleteAsync(url);
				_logger.LogInformation("Unsubscribing from Webhooks...");
			}

			var content = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError($"Webhook Management Error ({subscribe}): {content}");
				return false;
			}

			using var doc = JsonDocument.Parse(content);
			// Успешный ответ обычно: { "success": true }
			if (doc.RootElement.TryGetProperty("success", out var successProp))
			{
				return successProp.GetBoolean();
			}

			// Иногда ответ просто { "data": [] } при подписке, считаем успехом если 200 OK
			return true;
		}

		[HttpGet("auth/callback")]
		public async Task<IActionResult> Callback(string? code, string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return RedirectToAction("Profile", "Auth");

			try
			{
				// 1. Получаем Short Token
				var cleanCode = code.Replace("#_", "");
				var formData = new Dictionary<string, string>
				{
					{ "client_id", InstagramAppId },
					{ "client_secret", InstagramAppSecret },
					{ "grant_type", "authorization_code" },
					{ "redirect_uri", RedirectUri },
					{ "code", cleanCode }
				};

				var shortResp = await _httpClient.PostAsync("https://api.instagram.com/oauth/access_token", new FormUrlEncodedContent(formData));
				if (!shortResp.IsSuccessStatusCode)
				{
					_logger.LogError("Error getting short token");
					return RedirectToAction("Index");
				}

				using var shortDoc = JsonDocument.Parse(await shortResp.Content.ReadAsStringAsync());
				var shortToken = shortDoc.RootElement.GetProperty("access_token").GetString();

				// 2. Меняем на Long Token
				var longUrl = $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={InstagramAppSecret}&access_token={shortToken}";
				var longResp = await _httpClient.GetAsync(longUrl);
				if (!longResp.IsSuccessStatusCode)
				{
					_logger.LogError("Error getting long token");
					return RedirectToAction("Index");
				}

				using var longDoc = JsonDocument.Parse(await longResp.Content.ReadAsStringAsync());
				var longAccessToken = longDoc.RootElement.GetProperty("access_token").GetString();
				var expiresIn = longDoc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 5184000;
				var expireDate = DateTime.UtcNow.AddSeconds(expiresIn);

				// 3. Получаем данные пользователя (ID, Username, Avatar)
				// Запрашиваем поля: id, user_id (для Deauth), username, profile_picture_url
				var userUrl = $"https://graph.instagram.com/me?fields=id,user_id,username,profile_picture_url&access_token={longAccessToken}";
				var userResponse = await _httpClient.GetAsync(userUrl);

				var username = "Unknown";
				var instagramScopedUserId = ""; // Это user_id (для Deauth)
				var profilePicUrl = "";         // Ссылка на фото

				if (userResponse.IsSuccessStatusCode)
				{
					using var userDoc = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
					var root = userDoc.RootElement;

					if (root.TryGetProperty("username", out var u)) username = u.GetString();
					if (root.TryGetProperty("profile_picture_url", out var p)) profilePicUrl = p.GetString();
					if (root.TryGetProperty("user_id", out var i)) instagramScopedUserId = i.GetString();
				}

				// 4. Сохраняем в БД
				await SaveTokenToDatabase(longAccessToken, instagramScopedUserId, expireDate, profilePicUrl, username);

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Instagram Auth Error");
				return RedirectToAction("Index");
			}
		}

		/// <summary>
		/// Эндпоинт для деавторизации (Instagram вызывает при отзыве доступа)
		/// </summary>
		[AllowAnonymous]
		[HttpGet("deauth")]
		[HttpPost("deauth")]
		public async Task<IActionResult> DeauthorizationCallback([FromForm] string signed_request = null)
		{
			_logger.LogInformation($"=== Deauthorization callback received ===");

			try
			{
				if (string.IsNullOrEmpty(signed_request)) return Ok();

				var userId = ParseSignedRequest(signed_request);
				if (!string.IsNullOrEmpty(userId))
				{
					_logger.LogInformation($"User {userId} deauthorized app. Cleaning up token...");

					// Вызываем наш метод очистки (false = не удалять всё, только токен)
					await DisconnectInstagramUser(userId, fullDataDelete: false);
				}

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing deauthorization");
				return Ok();
			}
		}

		/// <summary>
		/// Эндпоинт для удаления данных пользователя (Data Deletion Request)
		/// </summary>
		[AllowAnonymous]
		[HttpGet("data-deletion")]
		[HttpPost("data-deletion")]
		public async Task<IActionResult> DataDeletionCallback(
			[FromForm] string signed_request = null)
		{
			_logger.LogInformation($"=== Data Deletion callback received ===");

			try
			{
				string userId = null;
				string confirmationCode = Guid.NewGuid().ToString("N");

				if (!string.IsNullOrEmpty(signed_request))
				{
					userId = ParseSignedRequest(signed_request);
				}

				if (!string.IsNullOrEmpty(userId))
				{
					_logger.LogInformation($"Processing FULL DATA DELETION for user: {userId}");

					// Удаляем данные полностью (true)
					await DisconnectInstagramUser(userId, fullDataDelete: true);
				}

				// Генерируем URL статуса (его нужно реализовать ниже)
				var statusUrl = $"{APP_URL}/instagram/deletion-status/{confirmationCode}";

				var response = new
				{
					url = statusUrl,
					confirmation_code = confirmationCode,
					status = "success" // Мы удалили данные синхронно, так что сразу success
				};

				return Ok(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing data deletion");
				return Ok(new { url = $"{APP_URL}", confirmation_code = "error", status = "error" });
			}
		}

		private string ParseSignedRequest(string signedRequest)
		{
			try
			{
				var parts = signedRequest.Split('.');
				if (parts.Length != 2) return null;

				var payload = parts[1].Replace('-', '+').Replace('_', '/');
				switch (payload.Length % 4)
				{
					case 2: payload += "=="; break;
					case 3: payload += "="; break;
				}

				var payloadBytes = Convert.FromBase64String(payload);
				var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);

				dynamic data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
				return data.user_id?.ToString();
			}
			catch
			{
				return null;
			}
		}

		[AllowAnonymous]
		[HttpGet("deletion-status/{code}")]
		public IActionResult DeletionStatus(string code)
		{
			var html = $@"
				<html>
					<head><title>Статус удаления данных</title></head>
					<body style='font-family: sans-serif; text-align: center; padding: 50px;'>
						<h1 style='color: green;'>Данные успешно удалены</h1>
						<p>Ваш запрос на удаление данных был обработан.</p>
						<p>Код подтверждения: <strong>{code}</strong></p>
						<p>Дата: {DateTime.UtcNow:g} (UTC)</p>
					</body>
				</html>";
			return Content(html, "text/html");
		}

		[HttpGet("facebook/auth/callback")]
		public async Task<IActionResult> FaceBookOAuthCallback(
			[FromQuery] string code,
			[FromQuery] string state = null,
			[FromQuery] string error = null,
			[FromQuery] string error_reason = null,
			[FromQuery] string error_description = null)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return RedirectToAction("Profile", "Auth");

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
									$"redirect_uri={FaceBookRedirectUri}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				var shortResponse = await _httpClient.GetAsync(shortTokenUrl);
				var shortJsonStr = await shortResponse.Content.ReadAsStringAsync();

				if (!shortResponse.IsSuccessStatusCode)
				{
					_logger.LogError("Не удалось обменять код на токен.");
					return RedirectToAction("Index");
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
					_logger.LogError($"Короткий токен получен, но обмен на длинный не удался.Short Token: {shortAccessToken} Error: {longJsonStr}");
					return RedirectToAction("Index");
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

				_logger.LogInformation($"longAccessToken = {longAccessToken} " +
					$"fbId ={fbId} internalUserId ={internalUserId} fbName = {fbName} expireDate = {expireDate:dd.MM.yyyy HH:mm}" +
					$"shortAccessToken = {shortAccessToken}");

				// D. Сохраняем
				await SaveTokenToDatabase(longAccessToken, fbId, expireDate, null, null);

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Facebook Auth Error");
				return RedirectToAction("Index");
			}
		}

		// =========================================================
		// ГЛАВНЫЙ МЕТОД СОХРАНЕНИЯ
		// =========================================================
		private async Task SaveTokenToDatabase(
			string accessToken,
			string instagramUserId, // Это user_id 
			DateTime expiresIn,
			string? profilePicUrl,
			string? username)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdStr)) return;

			var userId = int.Parse(userIdStr);

			var user = await _db.Users
				.Include(u => u.InstagramSettings)
				.FirstOrDefaultAsync(u => u.Id == userId);

			if (user == null) return;

			if (user.InstagramSettings == null)
			{
				user.InstagramSettings = new InstagramSettings { UserId = userId };
			}

			// Обновляем все поля
			user.InstagramSettings.AccessToken = accessToken;
			user.InstagramSettings.InstagramBusinessId = instagramUserId; // Теперь здесь правильный ID для удаления
			user.InstagramSettings.TokenExpiresAt = expiresIn;
			user.InstagramSettings.IsActive = false;

			// Новые поля
			user.InstagramSettings.ProfilePictureUrl = profilePicUrl;
			user.InstagramSettings.Username = username;

			await _db.SaveChangesAsync();
			_logger.LogInformation($"Token, Avatar and Username saved for User {userId} (Insta ID: {instagramUserId})");
		}

		private async Task<bool> DisconnectInstagramUser(string instagramUserId, bool fullDataDelete)
		{
			// Ищем настройки, где BusinessId совпадает с ID из вебхука
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == instagramUserId);

			if (settings == null)
			{
				_logger.LogWarning($"User with Instagram ID {instagramUserId} not found in DB.");
				return false;
			}

			if (fullDataDelete)
			{
				// ВАРИАНТ 1: Полное удаление настроек (Data Deletion)
				settings.AccessToken = null;
				settings.IsActive = false;
				settings.TokenExpiresAt = null;
				settings.InstagramBusinessId = null;
				settings.ProfilePictureUrl = null;
				settings.Username = null;

				_logger.LogInformation($"Instagram settings deleted for BusinessId: {instagramUserId}");
			}
			else
			{
				// ВАРИАНТ 2: Просто отзыв токена (Deauth)
				settings.AccessToken = null;
				settings.IsActive = false;
				settings.TokenExpiresAt = null;
				settings.ProfilePictureUrl = null;
				settings.Username = null;

				_logger.LogInformation($"Access Token cleared for BusinessId: {instagramUserId}");
			}

			await _db.SaveChangesAsync();
			return true;
		}
	}
}