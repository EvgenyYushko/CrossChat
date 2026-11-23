using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Helpers;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Interfaces.Google;
using CrossChat.Integrations.Models.Site;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using StackExchange.Redis;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Infrastructure.Constants.EnvConstants;
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

		private readonly IGoogleDriveUploader _googleDriveUploader;
		private readonly SiteSettings _siteSettings;
		private readonly IDatabase _redis;

		public InstagramController(
			ILogger<InstagramController> logger,
			IOptions<SocialMediaSettings> options,
			AppDbContext db,
			IInstagramService instagramService,
			IGoogleDriveUploader googleDriveUploader,
			SiteSettings siteSettings,
			IConnectionMultiplexer redisMux)
		{
			_logger = logger;
			_settings = options.Value;
			_db = db;
			_instagramService = instagramService;
			_googleDriveUploader = googleDriveUploader;
			_siteSettings = siteSettings;
			_httpClient = new HttpClient();
			_redis = redisMux.GetDatabase();
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
				// 1. Очистка публикаций планировщика и Google Drive
				await CleanupLinkedPostsAsync(NetworkType.Instagram, botId);

				// 2. 🧹 ОЧИСТКА ДИАЛОГОВ И СООБЩЕНИЙ ИНБОКСА:
				await CleanupLinkedInboxAsync(NetworkType.Instagram, botId);

				// 3. Отписка от вебхуков Meta
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

				// 4. Полное удаление аккаунта из БД
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
			string? dailyLimitPrompt,
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
			string? commentTemplates,
			bool isAutoMediaEnabled,
			int botFilterMode = 0,
			string? botWhitelist = null
			)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				settings.BotFilterMode = botFilterMode == 1 ? 1 : 0;
				settings.BotWhitelist = string.IsNullOrWhiteSpace(botWhitelist) ? null : botWhitelist.Trim();

				bool newIsActiveStatus = isDirectEnabled || isCommentsEnabled;
				settings.IsAutoMediaEnabled = isAutoMediaEnabled;
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
				settings.DailyLimitPrompt = string.IsNullOrWhiteSpace(dailyLimitPrompt) ? null : dailyLimitPrompt.Trim();

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

			// 🧹 ОЧИЩАЕМ ИНБОКС ПЕРЕД УДАЛЕНИЕМ:
			await CleanupLinkedInboxAsync(NetworkType.Instagram, settings.Id);

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
				var galleryDriveIds = await _db.InstagramMediaItems
					.AsNoTracking()
					.Where(m => m.InstagramSettingsId == settings.Id)
					.Select(m => m.GoogleDriveFileId)
					.ToListAsync();

				foreach (var driveId in galleryDriveIds)
				{
					try { await _googleDriveUploader.DeleteFileByIdAsync(driveId); } catch { }
				}

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

		// ==========================================================
		// РАБОТА С КОММЕНТАРИЯМИ ПОСТА (COMMUNITY MANAGEMENT)
		// ==========================================================

		/// <summary>
		/// 1. Получить дерево комментариев и ответов для поста
		/// </summary>
		[HttpGet("analytics/comments")]
		public async Task<IActionResult> GetPostComments([FromQuery] int botId, [FromQuery] string mediaId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return Unauthorized();

			var comments = await _instagramService.GetMediaCommentsAsync(mediaId, settings.AccessToken);
			return Json(comments);
		}

		/// <summary>
		/// 2. Ответить на конкретный комментарий пользователя (вложенный ответ)
		/// </summary>
		[HttpPost("analytics/comments/reply")]
		public async Task<IActionResult> ReplyToComment([FromBody] IgReplyCommentDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Text))
				return BadRequest("Текст ответа не может быть пустым.");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.Id == dto.BotId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return Unauthorized();

			var replyId = await _instagramService.ReplyToCommentAsync(dto.CommentId, dto.Text.Trim(), settings.AccessToken);
			if (string.IsNullOrEmpty(replyId))
				return BadRequest("Не удалось отправить ответ в Instagram.");

			return Ok(new { success = true, id = replyId });
		}

		/// <summary>
		/// 3. Опубликовать новый корневой комментарий под постом
		/// </summary>
		[HttpPost("analytics/comments/create")]
		public async Task<IActionResult> CreatePostComment([FromBody] IgCreateCommentDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Text))
				return BadRequest("Текст комментария не может быть пустым.");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.Id == dto.BotId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return Unauthorized();

			var commentId = await _instagramService.CreateCommentAsync(dto.MediaId, dto.Text.Trim(), settings.AccessToken);
			if (string.IsNullOrEmpty(commentId))
				return BadRequest("Не удалось опубликовать комментарий в Instagram.");

			return Ok(new { success = true, id = commentId });
		}

		/// <summary>
		/// 4. Удалить комментарий или ответ (модерация спама)
		/// </summary>
		[HttpPost("analytics/comments/delete")]
		public async Task<IActionResult> DeleteComment([FromBody] IgDeleteCommentDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.Id == dto.BotId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return Unauthorized();

			bool success = await _instagramService.DeleteCommentAsync(dto.CommentId, settings.AccessToken);
			if (!success)
				return BadRequest("Не удалось удалить комментарий в Instagram.");

			return Ok(new { success = true });
		}

		// ==========================================================
		// 🚀 СТРАНИЦА: АНАЛИЗ ДИАЛОГОВ И АУДИТОРИИ ИИ (/instagram/ai-chats)
		// ==========================================================
		[HttpGet("ai-chats")]
		public async Task<IActionResult> AiChats(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.AsNoTracking()
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null) return NotFound();

			var last24h = DateTime.UtcNow.AddDays(-1);

			// Загружаем клиентов со счетчиками за 1 быстрый SQL-запрос
			var customers = await _db.InstagramBotCustomers
				.AsNoTracking()
				.Where(c => c.InstagramSettingsId == botId)
				.Select(c => new InstagramCustomerAnalysisDto
				{
					CustomerId = c.Id,
					SenderId = c.InstagramSenderId,
					Username = c.Username,
					ProfilePictureUrl = c.ProfilePictureUrl,
					AppearanceDescription = c.AppearanceDescription,
					AiContext = c.AiContext ?? c.AppearanceDescription, // <-- ПОДТЯГИВАЕМ ДОСЬЕ ИЗ БД
					IsIgnored = c.IsIgnored,
					TotalResponsesCount = c.ResponseLogs.Count,
					TodayResponsesCount = c.ResponseLogs.Count(l => l.RespondedAt >= last24h),
					LastRespondedAt = c.ResponseLogs.Max(l => (DateTime?)l.RespondedAt),
					CreatedAt = c.CreatedAt,
					SentMediaCount = c.SentMediaHistory.Count
				})
				// 🚀 Сортировка: сверху те, кому ответили недавно, затем старые
				.OrderByDescending(c => c.LastRespondedAt ?? c.CreatedAt)
				.ToListAsync();

			foreach (var c in customers)
			{
				c.IsWhitelisted = InstagramWhitelistHelper.IsInWhitelist(settings.BotWhitelist, c.Username, c.SenderId);
			}

			ViewBag.BotFilterMode = settings.BotFilterMode;
			ViewBag.BotWhitelist = settings.BotWhitelist;

			ViewBag.Settings = settings;
			ViewBag.BotId = botId;

			ViewBag.TotalGalleryItemsCount = await _db.InstagramMediaItems
				.AsNoTracking()
				.CountAsync(m => m.InstagramSettingsId == botId);

			return View("AiChats", customers);
		}

		/// <summary>
		/// Ленивая AJAX-подгрузка сообщений из Инбокса при раскрытии строки (0 мс задержки для таблицы!)
		/// </summary>
		[HttpGet("ai-chats/messages")]
		public async Task<IActionResult> GetCustomerChatMessages([FromQuery] int botId, [FromQuery] string senderId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var isOwner = await _db.InstagramSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
			if (!isOwner) return Unauthorized();

			var conversation = await _db.InboxConversations
				.AsNoTracking()
				.Include(c => c.Messages)
				.FirstOrDefaultAsync(c => c.NetworkType == (int)NetworkType.Instagram &&
										  c.BotId == botId &&
										  c.ExternalUserId == senderId);

			if (conversation == null || !conversation.Messages.Any())
			{
				return Json(new { messages = new List<object>() });
			}

			// Берем последние 20 сообщений (хронологически)
			var messages = conversation.Messages
				.OrderBy(m => m.CreatedAtUtc)
				.TakeLast(20)
				.Select(m => new
				{
					id = m.Id,
					text = m.Text,
					isIncoming = m.IsIncoming,
					isSentByBot = m.IsSentByBot,
					time = m.CreatedAtUtc.ToString("dd.MM HH:mm"),
					mediaUrl = m.MediaUrl,
					mediaType = m.MediaType
				})
				.ToList();

			return Json(new { messages, conversationId = conversation.Id });
		}

		// ==========================================================
		// 🖼️ МЕДИАТЕКА БОТА (КАТЕГОРИИ + ФОТО/ВИДЕО НА GOOGLE DRIVE)
		// ==========================================================
		[HttpGet("media-gallery")]
		public async Task<IActionResult> MediaGallery(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null) return NotFound();

			// Гарантируем наличие дефолтной категории "Общее"
			bool hasDefault = await _db.InstagramMediaCategories
				.AnyAsync(c => c.InstagramSettingsId == botId && c.IsDefault);

			if (!hasDefault)
			{
				_db.InstagramMediaCategories.Add(new InstagramMediaCategory
				{
					InstagramSettingsId = botId,
					Name = "Общее",
					Description = "Общие фото и видео, селфи, лайфстайл",
					IsDefault = true
				});
				await _db.SaveChangesAsync();
			}

			var categories = await _db.InstagramMediaCategories
				.AsNoTracking()
				.Where(c => c.InstagramSettingsId == botId)
				.Include(c => c.Items)
					.ThenInclude(i => i.SentHistory)
				.OrderByDescending(c => c.IsDefault)
				.ThenBy(c => c.CreatedAtUtc)
				.ToListAsync();

			ViewBag.Categories = categories;
			return View("MediaGallery", settings);
		}

		[HttpPost("media-gallery/toggle-auto")]
		public async Task<IActionResult> ToggleAutoMedia([FromBody] ToggleAutoMediaDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == dto.BotId && s.UserId == userId);

			if (settings == null) return Unauthorized();

			settings.IsAutoMediaEnabled = dto.Enabled;
			await _db.SaveChangesAsync();

			return Ok(new { success = true, enabled = settings.IsAutoMediaEnabled });
		}

		[HttpPost("media-gallery/categories/create")]
		public async Task<IActionResult> CreateMediaCategory([FromBody] CreateMediaCategoryDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Name))
				return BadRequest(new { success = false, error = "Введите название категории" });

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			bool isOwner = await _db.InstagramSettings.AnyAsync(s => s.Id == dto.BotId && s.UserId == userId);
			if (!isOwner) return Unauthorized();

			var category = new InstagramMediaCategory
			{
				InstagramSettingsId = dto.BotId,
				Name = dto.Name.Trim(),
				Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
				IsDefault = false
			};

			_db.InstagramMediaCategories.Add(category);
			await _db.SaveChangesAsync();

			return Ok(new { success = true, id = category.Id });
		}

		[HttpPost("media-gallery/categories/delete")]
		public async Task<IActionResult> DeleteMediaCategory([FromBody] DeleteMediaCategoryDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var category = await _db.InstagramMediaCategories
				.Include(c => c.InstagramSettings)
				.Include(c => c.Items)
				.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.InstagramSettings.UserId == userId);

			if (category == null) return NotFound();
			if (category.IsDefault) return BadRequest(new { success = false, error = "Дефолтную категорию нельзя удалить." });

			foreach (var item in category.Items)
			{
				try { await _googleDriveUploader.DeleteFileByIdAsync(item.GoogleDriveFileId); } catch { }
			}

			_db.InstagramMediaCategories.Remove(category);
			await _db.SaveChangesAsync();

			return Ok(new { success = true });
		}

		[HttpPost("media-gallery/upload")]
		[RequestSizeLimit(100 * 1024 * 1024)]
		public async Task<IActionResult> UploadGalleryMedia(
			[FromForm] int botId,
			[FromForm] int categoryId,
			[FromForm] List<IFormFile> files)
		{
			if (files == null || files.Count == 0)
				return BadRequest(new { success = false, error = "Файлы не выбраны." });

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var category = await _db.InstagramMediaCategories
				.Include(c => c.InstagramSettings)
				.FirstOrDefaultAsync(c => c.Id == categoryId &&
											c.InstagramSettingsId == botId &&
											c.InstagramSettings.UserId == userId);

			if (category == null) return NotFound(new { success = false, error = "Категория не найдена." });

			if (!Directory.Exists(_siteSettings.TempFolder))
				Directory.CreateDirectory(_siteSettings.TempFolder);

			int uploadedCount = 0;
			var errors = new List<string>();

			foreach (var file in files)
			{
				if (file.Length == 0) continue;

				var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
				bool isVideo = ext is ".mp4" or ".mov";
				bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".webp";

				if (!isVideo && !isImage)
				{
					errors.Add($"{file.FileName}: неподдерживаемый формат (разрешены jpg, png, webp, mp4, mov).");
					continue;
				}

				string tempPath = Path.Combine(_siteSettings.TempFolder, $"gal_up_{Guid.NewGuid():N}{ext}");
				try
				{
					using (var fs = new FileStream(tempPath, FileMode.Create))
					{
						await file.CopyToAsync(fs);
					}

					if (isVideo)
					{
						// Сжимаем для лимитов Instagram Direct (если > 20 МБ) и срезаем метаданные ИИ
						tempPath = await VideoService.CompressVideoForDirectAsync(tempPath, _logger);
						await VideoService.StripAiMetadataAsync(tempPath, _logger);
					}

					string? thumbBase64 = await VideoService.CreateGalleryThumbnailBase64Async(tempPath, isVideo);
					long finalSize = new FileInfo(tempPath).Length;
					string mimeType = isVideo ? "video/mp4" : (ext == ".png" ? "image/png" : "image/jpeg");
					string driveFileName = $"ig_bot_{botId}_cat_{categoryId}_{Guid.NewGuid():N}{(isVideo ? ".mp4" : ext)}";

					string driveFileId;
					using (var uploadStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
					{
						driveFileId = await _googleDriveUploader.UploadStreamAsync(
							uploadStream,
							driveFileName,
							GOOGLE_POSTS_FOLDER_ID,
							mimeType);
					}

					var item = new InstagramMediaItem
					{
						InstagramSettingsId = botId,
						CategoryId = categoryId,
						MediaType = isVideo ? "video" : "image",
						GoogleDriveFileId = driveFileId,
						FileName = Path.GetFileName(file.FileName),
						MimeType = mimeType,
						FileSizeBytes = finalSize,
						ThumbnailBase64 = thumbBase64
					};

					_db.InstagramMediaItems.Add(item);
					await _db.SaveChangesAsync();
					uploadedCount++;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[Media Gallery] Ошибка загрузки файла {Name}", file.FileName);
					errors.Add($"{file.FileName}: {ex.Message}");
				}
				finally
				{
					if (System.IO.File.Exists(tempPath))
					{
						try { System.IO.File.Delete(tempPath); } catch { }
					}
				}
			}

			return Ok(new { success = uploadedCount > 0, uploadedCount, errors });
		}

		[HttpPost("media-gallery/items/delete")]
		public async Task<IActionResult> DeleteGalleryItem([FromBody] DeleteGalleryItemDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var item = await _db.InstagramMediaItems
				.Include(i => i.Category)
					.ThenInclude(c => c.InstagramSettings)
				.FirstOrDefaultAsync(i => i.Id == dto.ItemId && i.Category.InstagramSettings.UserId == userId);

			if (item == null) return NotFound();

			try { await _googleDriveUploader.DeleteFileByIdAsync(item.GoogleDriveFileId); } catch { }

			_db.InstagramMediaItems.Remove(item);
			await _db.SaveChangesAsync();

			return Ok(new { success = true });
		}

		[HttpPost("ai-chats/toggle-ignore")]
		public async Task<IActionResult> ToggleCustomerIgnore([FromBody] ToggleCustomerFlagDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var customer = await _db.InstagramBotCustomers
				.Include(c => c.InstagramSettings)
				.FirstOrDefaultAsync(c => c.Id == dto.CustomerId &&
											c.InstagramSettingsId == dto.BotId &&
											c.InstagramSettings.UserId == userId);

			if (customer == null) return NotFound();

			customer.IsIgnored = !customer.IsIgnored;
			await _db.SaveChangesAsync();

			return Ok(new { success = true, isIgnored = customer.IsIgnored });
		}

		[HttpPost("ai-chats/toggle-whitelist")]
		public async Task<IActionResult> ToggleCustomerWhitelist([FromBody] ToggleCustomerFlagDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var customer = await _db.InstagramBotCustomers
				.Include(c => c.InstagramSettings)
				.FirstOrDefaultAsync(c => c.Id == dto.CustomerId &&
											c.InstagramSettingsId == dto.BotId &&
											c.InstagramSettings.UserId == userId);

			if (customer == null) return NotFound();

			var (newList, isAdded) = InstagramWhitelistHelper.Toggle(
				customer.InstagramSettings.BotWhitelist,
				customer.Username,
				customer.InstagramSenderId);

			customer.InstagramSettings.BotWhitelist = string.IsNullOrWhiteSpace(newList) ? null : newList;
			await _db.SaveChangesAsync();

			return Ok(new { success = true, isWhitelisted = isAdded, whitelist = customer.InstagramSettings.BotWhitelist });
		}

		[HttpPost("ai-chats/set-filter-mode")]
		public async Task<IActionResult> SetFilterMode([FromBody] SetBotFilterModeDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.Id == dto.BotId && s.UserId == userId);

			if (settings == null) return Unauthorized();

			settings.BotFilterMode = dto.Mode == 1 ? 1 : 0;
			await _db.SaveChangesAsync();

			return Ok(new { success = true, mode = settings.BotFilterMode });
		}

		[HttpPost("ai-chats/reset-sent-media")]
		public async Task<IActionResult> ResetCustomerSentMedia([FromBody] ToggleCustomerFlagDto dto)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var customer = await _db.InstagramBotCustomers
				.Include(c => c.InstagramSettings)
				.FirstOrDefaultAsync(c => c.Id == dto.CustomerId &&
											c.InstagramSettingsId == dto.BotId &&
											c.InstagramSettings.UserId == userId);

			if (customer == null) return NotFound();

			// 1. Удаляем всю историю отправленных этому клиенту фото/видео
			int deletedCount = await _db.InstagramCustomerSentMedias
				.Where(s => s.CustomerId == customer.Id)
				.ExecuteDeleteAsync();

			// 2. Убираем секцию GALLERY MEDIA STATS из AiContext (сохраняя профиль и долгую память!)
			customer.AiContext = StripMediaStatsSection(customer.AiContext);
			await _db.SaveChangesAsync();

			// 3. Сбрасываем кэш контекста и 20-минутный кулдаун в Redis, чтобы можно было тестить сразу
			await _redis.KeyDeleteAsync($"ig:user_context:{customer.InstagramSenderId}");
			if (!string.IsNullOrEmpty(customer.InstagramSettings.InstagramBusinessId))
			{
				await _redis.KeyDeleteAsync($"media:cooldown:{customer.InstagramSettings.InstagramBusinessId}:{customer.InstagramSenderId}");
			}

			_logger.LogInformation("🔄 [Media Reset] Сброшена история медиа ({Count} шт.) для @{User}", deletedCount, customer.Username);

			return Ok(new
			{
				success = true,
				deletedCount,
				aiContext = customer.AiContext
			});
		}

		private static string? StripMediaStatsSection(string? aiContext)
		{
			if (string.IsNullOrWhiteSpace(aiContext)) return aiContext;

			const string marker = "GALLERY MEDIA STATS:";
			int idx = aiContext.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (idx < 0) return aiContext;

			string cleaned = aiContext.Substring(0, idx).Trim();
			return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
		}

		public class ToggleCustomerFlagDto { public int BotId { get; set; } public int CustomerId { get; set; } }
		public class SetBotFilterModeDto { public int BotId { get; set; } public int Mode { get; set; } }

		public class ToggleAutoMediaDto { public int BotId { get; set; } public bool Enabled { get; set; } }
		public class CreateMediaCategoryDto { public int BotId { get; set; } public string Name { get; set; } = string.Empty; public string? Description { get; set; } }
		public class DeleteMediaCategoryDto { public int CategoryId { get; set; } }
		public class DeleteGalleryItemDto { public int ItemId { get; set; } }

		public class InstagramCustomerAnalysisDto
		{
			public int CustomerId { get; set; }
			public string SenderId { get; set; } = string.Empty;
			public string? Username { get; set; }
			public string? ProfilePictureUrl { get; set; }
			public string? AppearanceDescription { get; set; }
			public bool IsIgnored { get; set; }
			public int TotalResponsesCount { get; set; }
			public int TodayResponsesCount { get; set; }
			public DateTime? LastRespondedAt { get; set; }
			public DateTime CreatedAt { get; set; }
			public string? AiContext { get; set; }
			public bool IsWhitelisted { get; set; }
			public int SentMediaCount { get; set; }
		}
	}

	public class IgReplyCommentDto
	{
		public int BotId { get; set; }
		public string CommentId { get; set; } = string.Empty;
		public string Text { get; set; } = string.Empty;
	}

	public class IgCreateCommentDto
	{
		public int BotId { get; set; }
		public string MediaId { get; set; } = string.Empty;
		public string Text { get; set; } = string.Empty;
	}

	public class IgDeleteCommentDto
	{
		public int BotId { get; set; }
		public string CommentId { get; set; } = string.Empty;
	}
}