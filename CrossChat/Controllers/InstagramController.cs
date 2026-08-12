using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;
using static CrossChat.Helpers.TimeZoneHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("instagram")]
	public class InstagramController : BaseController
	{
		private readonly ILogger<InstagramController> _logger;
		private readonly SocialMediaSettings _settings;
		private readonly HttpClient _httpClient;
		private readonly AppDbContext _db;
		private readonly IInstagramService _instagramService;
		private const string GraphApiVersion = "v21.0";
		private string InstagramAppId => _settings.InstagramAppId;
		private string InstagramAppSecret => _settings.InstagramAppSecret;

		private string RedirectUri => $"{APP_URL}/instagram/auth/callback";

		public InstagramController(
			ILogger<InstagramController> logger,
			IOptions<SocialMediaSettings> options,
			AppDbContext db,
			IInstagramService instagramService)
		{
			_logger = logger;
			_settings = options.Value;
			_db = db;
			_instagramService = instagramService;
			_httpClient = new HttpClient();
		}

		// ==========================================================
		// 1. ГЛАВНАЯ СТРАНИЦА НАСТРОЕК (/instagram)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Загружаем настройки, чтобы передать их во View
			var settings = await _db.InstagramSettings
				.Include(p => p.Profile)
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

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

			return View(settings);
		}

		// ==========================================================
		// 3. ОТКЛЮЧЕНИЕ АККАУНТА (ПОЛЬЗОВАТЕЛЕМ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

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

				await DisconnectInstagramUser(settings.InstagramBusinessId, fullDataDelete: true);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 2. ОБНОВЛЕНИЕ НАСТРОЕК (ПРОМПТ / ВКЛЮЧЕНИЕ)
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, bool isDirectEnabled,
			bool isCommentsEnabled,
			bool processPhotos,
			bool processVideos,
			bool processAudios,
			string systemPrompt,
			string commentPrompt,
			bool isReactionsEnabled,
			string allowedReactions,
			int maxAnswerMessagesCount,
			int maxAnswersTokensCount, 
			int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Ищем настройки бота по его Id И проверяем, что он принадлежит текущему юзеру
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				// 1. Вычисляем новый общий статус активности
				bool newIsActiveStatus = isDirectEnabled || isCommentsEnabled;

				// 2. Управление вебхуками (если статус изменился)
				if (settings.IsActive != newIsActiveStatus)
				{
					_logger.LogInformation($"Изменение статуса вебхуков для бота {botId} (User {userId}): {settings.IsActive} -> {newIsActiveStatus}");

					bool success = await ManageWebhooksAsync(settings.AccessToken, newIsActiveStatus);
					if (!success)
					{
						_logger.LogWarning($"[Meta API] Не удалось обновить подписку на вебхуки для бота {botId}");
					}
				}

				// 3. Обновляем модель
				settings.IsActive = newIsActiveStatus;
				settings.IsDirectEnabled = isDirectEnabled;
				settings.IsCommentsEnabled = isCommentsEnabled;

				settings.SystemPrompt = systemPrompt ?? "";
				settings.CommentPrompt = commentPrompt ?? "";

				settings.ProcessPhotos = processPhotos;
				settings.ProcessVideos = processVideos;
				settings.ProcessAudios = processAudios;

				settings.MaxAnswerMessagesCount = maxAnswerMessagesCount;
				settings.MaxAnswersTokensCount = maxAnswersTokensCount;
				settings.ProfileId = profileId;

				var reactionList = allowedReactions?.EnumerateRunes()
					.Select(r => r.ToString())
					.Where(s => !string.IsNullOrWhiteSpace(s))
					.ToList();

				settings.IsReactionsEnabled = isReactionsEnabled;
				settings.AllowedReactions = reactionList is not null ? string.Join(",", reactionList) : "";

				await _db.SaveChangesAsync();
				_logger.LogInformation($"Настройки бота {botId} успешно сохранены.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Ошибка при обновлении настроек бота {botId}");
			}

			return RedirectToAction("Index", new { botId = botId });
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
				var expireDate = DateTimeNow.AddSeconds(expiresIn);

				// 3. Получаем данные пользователя (ID, Username, Avatar)
				(string? username, string? instagramScopedUserId, string? profilePicUrl) = await _instagramService.GetMeInfo(longAccessToken);

				// 4. Сохраняем в БД
				var instaSettings = await SaveTokenToDatabase(longAccessToken, instagramScopedUserId, expireDate, profilePicUrl, username);

				return RedirectToAction("Index", new { botId = instaSettings?.Id ?? 0 });
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

				var instagramUserId = ParseSignedRequest(signed_request);
				if (!string.IsNullOrEmpty(instagramUserId))
				{
					_logger.LogInformation($"User {instagramUserId} deauthorized app. Cleaning up token...");

					// Вызываем наш метод очистки (false = не удалять всё, только токен)
					await DisconnectInstagramUser(instagramUserId, fullDataDelete: true);
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
						<p>Дата: {DateTimeNow:g} (UTC)</p>
					</body>
				</html>";
			return Content(html, "text/html");
		}



		// =========================================================
		// ГЛАВНЫЙ МЕТОД СОХРАНЕНИЯ
		// =========================================================
		private async Task<InstagramSettings> SaveTokenToDatabase(
			string accessToken,
			string instagramUserId,
			DateTime expiresIn,
			string? profilePicUrl,
			string? username)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdStr)) return null;

			var userId = int.Parse(userIdStr);

			// 1. Ищем, нет ли у этого пользователя уже настроек для этого конкретного Instagram-аккаунта
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.InstagramBusinessId == instagramUserId);

			// 2. Если такого бота еще нет в базе — создаем нового
			if (settings == null)
			{
				settings = new InstagramSettings
				{
					UserId = userId,
					InstagramBusinessId = instagramUserId,
					ProfileId = GetActiveProfileId().Value
				};
				_db.InstagramSettings.Add(settings);
			}

			// 3. Скачиваем картинку в Base64
			string? base64Icon = null;
			if (!string.IsNullOrEmpty(profilePicUrl))
			{
				base64Icon = await DownloadImageAsBase64ForHtml(profilePicUrl);
			}

			// 4. Обновляем данные бота
			settings.AccessToken = accessToken;
			settings.TokenExpiresAt = expiresIn;
			settings.Username = username;

			if (base64Icon != null)
			{
				settings.ProfilePictureUrl = base64Icon;
			}

			await _db.SaveChangesAsync();
			_logger.LogInformation($"Token and settings saved for Bot {instagramUserId}, User {userId}");

			return settings;
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

			if (!string.IsNullOrEmpty(settings.AccessToken))
			{
				try
				{
					// false = отписка (DELETE запрос)
					// Мы не проверяем результат (true/false), потому что если юзер уже отозвал права,
					// этот запрос вернет ошибку (Invalid Token), и это НОРМАЛЬНО.
					await ManageWebhooksAsync(settings.AccessToken, false);
					_logger.LogInformation($"Unsubscribe request sent for {instagramUserId}");
				}
				catch (Exception ex)
				{
					// Логируем, но не останавливаем удаление данных из БД
					_logger.LogWarning($"Could not unsubscribe webhooks (token might be invalid): {ex.Message}");
				}
			}

			if (fullDataDelete)
			{
				// ВАРИАНТ 1: Полное удаление настроек (Data Deletion)
				_db.InstagramSettings.Remove(settings);
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

		[HttpPost("change-profile")]
		[Authorize]
		public async Task<IActionResult> ChangeProfile(int botId, int targetProfileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// 1. Проверяем, что бот принадлежит нам
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			// 2. Проверяем, что целевой профиль тоже принадлежит нам
			var profileExists = await _db.Profile
				.AnyAsync(p => p.Id == targetProfileId && p.UserId == userId);

			if (settings != null && profileExists)
			{
				settings.ProfileId = targetProfileId; // Магия: просто меняем ID профиля
				await _db.SaveChangesAsync();

				_logger.LogInformation($"[Instagram] Бот {botId} перенесен в профиль {targetProfileId}");
				return RedirectToAction("Index", new { botId = botId, saved = "true" });
			}

			return BadRequest("Не удалось перенести бота.");
		}
	}
}