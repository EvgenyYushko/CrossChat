using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Helpers.TimeZoneHelper;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

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

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.Include(p => p.Profile)
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			// ИСПОЛЬЗУЕМ МЕТОД ИЗ BaseController: считаем посты для умного предупреждения в UI
			ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.Instagram, botId);

			var instaScopes = string.Join(",",
				"instagram_business_basic",
				"instagram_business_manage_messages",
				"instagram_business_manage_comments",
				"instagram_business_content_publish",
				"instagram_business_manage_insights"
			);
			ViewBag.InstaLoginUrl = $"https://www.instagram.com/oauth/authorize?" +
						   $"client_id={InstagramAppId}&" +
						   $"redirect_uri={RedirectUri}&" +
						   $"response_type=code&" +
						   $"force_reauth=true&" +
						   $"scope={instaScopes}";

			return View(settings);
		}

		// ==========================================================
		// 2. ОТКЛЮЧЕНИЕ АККАУНТА (С УМНОЙ ОЧИСТКОЙ ПОСТОВ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ В ПЛАНИРОВЩИКЕ И GOOGLE DRIVE ИЗ BaseController:
				await CleanupLinkedPostsAsync(NetworkType.Instagram, botId);

				// 2. Отписка от вебхуков Meta
				if (!string.IsNullOrEmpty(settings.AccessToken))
				{
					try
					{
						await ManageWebhooksAsync(settings.AccessToken, false);
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Could not unsubscribe before disconnect. proceeding anyway.");
					}
				}

				// 3. Полное удаление аккаунта из БД
				await DisconnectInstagramUser(settings.InstagramBusinessId, fullDataDelete: true);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 3. ОБНОВЛЕНИЕ НАСТРОЕК
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId,
			bool isDirectEnabled,
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
			int profileId,
			bool isDailyStoriesEnabled,
			string dailyStoryTime,
			bool isStoryOverlayTextEnabled,
			string? storyOverlayText,
			int commentReplyMode,
			string? commentTemplates)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				bool newIsActiveStatus = isDirectEnabled || isCommentsEnabled;

				if (settings.IsActive != newIsActiveStatus)
				{
					_logger.LogInformation($"Изменение статуса вебхуков для бота {botId} (User {userId}): {settings.IsActive} -> {newIsActiveStatus}");

					bool success = await ManageWebhooksAsync(settings.AccessToken, newIsActiveStatus);
					if (!success)
					{
						_logger.LogWarning($"[Meta API] Не удалось обновить подписку на вебхуки для бота {botId}");
					}
				}

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

				settings.IsDailyStoriesEnabled = isDailyStoriesEnabled;
				settings.DailyStoryTime = string.IsNullOrWhiteSpace(dailyStoryTime) ? "12:00" : dailyStoryTime.Trim();

				settings.IsStoryOverlayTextEnabled = isStoryOverlayTextEnabled;
				settings.StoryOverlayText = string.IsNullOrWhiteSpace(storyOverlayText) ? null : storyOverlayText.Trim();

				settings.CommentReplyMode = commentReplyMode > 0 ? commentReplyMode : 2;
				settings.CommentTemplates = commentTemplates;

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

		private async Task<bool> ManageWebhooksAsync(string accessToken, bool subscribe)
		{
			var url = $"https://graph.instagram.com/{GraphApiVersion}/me/subscribed_apps?access_token={accessToken}";
			HttpResponseMessage response;

			if (subscribe)
			{
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
			}
			else
			{
				response = await _httpClient.DeleteAsync(url);
			}

			var content = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError($"Webhook Management Error ({subscribe}): {content}");
				return false;
			}

			using var doc = JsonDocument.Parse(content);
			if (doc.RootElement.TryGetProperty("success", out var successProp))
			{
				return successProp.GetBoolean();
			}

			return true;
		}

		[HttpGet("auth/callback")]
		public async Task<IActionResult> Callback(string? code, string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return RedirectToAction("Profile", "Auth");

			try
			{
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
				// СТРОГО UTC ДЛЯ ТОКЕНОВ:
				var expireDate = DateTime.UtcNow.AddSeconds(expiresIn);

				(string? username, string? instagramScopedUserId, string? profilePicUrl) = await _instagramService.GetMeInfo(longAccessToken);

				var instaSettings = await SaveTokenToDatabase(longAccessToken, instagramScopedUserId!, expireDate, profilePicUrl, username);

				return RedirectToAction("Index", new { botId = instaSettings?.Id ?? 0 });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Instagram Auth Error");
				return RedirectToAction("Index");
			}
		}

		[AllowAnonymous]
		[HttpGet("deauth")]
		[HttpPost("deauth")]
		public async Task<IActionResult> DeauthorizationCallback([FromForm] string signed_request = null!)
		{
			try
			{
				if (string.IsNullOrEmpty(signed_request)) return Ok();

				var instagramUserId = ParseSignedRequest(signed_request);
				if (!string.IsNullOrEmpty(instagramUserId))
				{
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

		[AllowAnonymous]
		[HttpGet("data-deletion")]
		[HttpPost("data-deletion")]
		public async Task<IActionResult> DataDeletionCallback([FromForm] string signed_request = null!)
		{
			_logger.LogInformation($"=== Data Deletion callback received ===");

			try
			{
				string? userId = null;
				string confirmationCode = Guid.NewGuid().ToString("N");

				if (!string.IsNullOrEmpty(signed_request))
				{
					userId = ParseSignedRequest(signed_request);
				}

				if (!string.IsNullOrEmpty(userId))
				{
					await DisconnectInstagramUser(userId, fullDataDelete: true);
				}

				var statusUrl = $"{APP_URL}/instagram/deletion-status/{confirmationCode}";
				return Ok(new { url = statusUrl, confirmation_code = confirmationCode, status = "success" });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing data deletion");
				return Ok(new { url = $"{APP_URL}", confirmation_code = "error", status = "error" });
			}
		}

		private string? ParseSignedRequest(string signedRequest)
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
				var payloadJson = Encoding.UTF8.GetString(payloadBytes);

				dynamic? data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
				return data?.user_id?.ToString();
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

		private async Task<InstagramSettings?> SaveTokenToDatabase(
			string accessToken,
			string instagramUserId,
			DateTime expiresIn,
			string? profilePicUrl,
			string? username)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdStr)) return null;

			var userId = int.Parse(userIdStr);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.InstagramBusinessId == instagramUserId);

			if (settings == null)
			{
				// БЕЗОПАСНЫЙ ПРОФИЛЬ ИЗ BaseController (БЕЗ ОШИБОК NULLABLE):
				int profileId = await GetActiveProfileIdSafeAsync(_db, userId);

				settings = new InstagramSettings
				{
					UserId = userId,
					InstagramBusinessId = instagramUserId,
					ProfileId = profileId
				};
				_db.InstagramSettings.Add(settings);
			}

			string? base64Icon = null;
			if (!string.IsNullOrEmpty(profilePicUrl))
			{
				base64Icon = await DownloadImageAsBase64ForHtml(profilePicUrl);
			}

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
					await ManageWebhooksAsync(settings.AccessToken, false);
				}
				catch (Exception ex)
				{
					_logger.LogWarning($"Could not unsubscribe webhooks: {ex.Message}");
				}
			}

			if (fullDataDelete)
			{
				_db.InstagramSettings.Remove(settings);
				_logger.LogInformation($"Instagram settings deleted for BusinessId: {instagramUserId}");
			}
			else
			{
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

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			var profileExists = await _db.Profile
				.AnyAsync(p => p.Id == targetProfileId && p.UserId == userId);

			if (settings != null && profileExists)
			{
				settings.ProfileId = targetProfileId;
				await _db.SaveChangesAsync();

				_logger.LogInformation($"[Instagram] Бот {botId} перенесен в профиль {targetProfileId}");
				return RedirectToAction("Index", new { botId = botId, saved = "true" });
			}

			return BadRequest("Не удалось перенести бота.");
		}

		[HttpGet("analytics")]
		public async Task<IActionResult> Analytics(int botId, string? after = null, string? before = null)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			{
				return RedirectToAction("Index");
			}

			ViewBag.BotId = botId;
			ViewBag.Username = settings.Username;
			ViewBag.AvatarUrl = settings.ProfilePictureUrl;

			var feedTask = _instagramService.GetAccountFeedAsync(settings.AccessToken, 24, after, before);
			var insightsTask = _instagramService.GetAccountInsightsAsync(settings.AccessToken);
			var profileTask = _httpClient.GetAsync($"https://graph.instagram.com/v21.0/me?fields=followers_count,media_count&access_token={settings.AccessToken}");

			await Task.WhenAll(feedTask, insightsTask, profileTask);

			ViewBag.AccountInsights = await insightsTask;

			int followersCount = 0;
			try
			{
				var profileResp = await profileTask;
				if (profileResp.IsSuccessStatusCode)
				{
					var json = await profileResp.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(json);
					if (doc.RootElement.TryGetProperty("followers_count", out var fc))
					{
						followersCount = fc.GetInt32();
					}
				}
			}
			catch { }

			ViewBag.FollowersCount = followersCount;

			return View(await feedTask);
		}

		[HttpGet("analytics/insights")]
		public async Task<IActionResult> GetPostInsights(int botId, string mediaId, string mediaType)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			{
				return Unauthorized();
			}

			var insights = await _instagramService.GetMediaInsightsAsync(mediaId, mediaType, settings.AccessToken);
			return Json(insights);
		}		
	}
}