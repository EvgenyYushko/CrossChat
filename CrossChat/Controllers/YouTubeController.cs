using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("youtube")]
	public class YouTubeController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly IYouTubeService _youTubeService;
		private readonly ILogger<YouTubeController> _logger;

		public YouTubeController(
			AppDbContext db,
			IYouTubeService youTubeService,
			ILogger<YouTubeController> logger)
		{
			_db = db;
			_youTubeService = youTubeService;
			_logger = logger;
		}

		private string GetRedirectUri() => $"{Request.Scheme}://{Request.Host}/youtube/callback";

		// ==========================================================
		// 1. СТАРТ АВТОРИЗАЦИИ (GOOGLE OAUTH 2.0)
		// ==========================================================
		[HttpGet("connect")]
		public async Task<IActionResult> Connect([FromQuery] int? profileId)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdStr)) return RedirectToAction("Login", "Auth");
			int userId = int.Parse(userIdStr);

			// БЕЗОПАСНОЕ ПОЛУЧЕНИЕ ПРОФИЛЯ ИЗ BaseController
			int activeProfileId = profileId ?? await GetActiveProfileIdSafeAsync(_db, userId);
			if (activeProfileId == 0) return RedirectToAction("Profile", "Auth");

			string state = activeProfileId.ToString();
			string authUrl = _youTubeService.GetAuthorizationUrl(state, GetRedirectUri());

			return Redirect(authUrl);
		}

		// ==========================================================
		// 2. КОЛЛБЭК ОТ GOOGLE (СОХРАНЕНИЕ ТОКЕНОВ В UTC)
		// ==========================================================
		[HttpGet("callback")]
		public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				_logger.LogWarning("[YouTube] Отказ в доступе или ошибка авторизации: {Err}", error);
				return RedirectToAction("Profile", "Auth");
			}

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			if (!int.TryParse(state, out int profileId) || profileId == 0)
			{
				profileId = await GetActiveProfileIdSafeAsync(_db, userId);
			}

			var tokens = await _youTubeService.ExchangeCodeForTokensAsync(code, GetRedirectUri());
			if (tokens == null)
			{
				TempData["Error"] = "Не удалось получить токены от Google";
				return RedirectToAction("Profile", "Auth");
			}

			var channelInfo = await _youTubeService.GetChannelInfoAsync(tokens.Value.AccessToken);
			if (channelInfo == null)
			{
				TempData["Error"] = "Не найден YouTube канал в данном Google-аккаунте.";
				return RedirectToAction("Profile", "Auth");
			}

			var existing = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.ChannelId == channelInfo.ChannelId && y.UserId == userId);

			string? base64Avatar = null;
			if (!string.IsNullOrEmpty(channelInfo.AvatarUrl))
			{
				base64Avatar = await DownloadImageAsBase64ForHtml(channelInfo.AvatarUrl);
			}

			// ВАЖНО: Срок годности токена строго в UTC!
			var tokenExpireDateUtc = DateTime.UtcNow.AddSeconds(tokens.Value.ExpiresIn);

			if (existing == null)
			{
				existing = new YouTubeSettings
				{
					UserId = userId,
					ProfileId = profileId,
					ChannelId = channelInfo.ChannelId,
					ChannelTitle = channelInfo.Title,
					CustomUrl = channelInfo.CustomUrl,
					ProfilePictureUrl = base64Avatar ?? channelInfo.AvatarUrl,
					AccessToken = tokens.Value.AccessToken,
					RefreshToken = tokens.Value.RefreshToken,
					TokenExpiresAt = tokenExpireDateUtc,
					SubscriberCount = channelInfo.SubscriberCount,
					VideoCount = channelInfo.VideoCount,
					IsActive = true
				};
				_db.YouTubeSettings.Add(existing);
			}
			else
			{
				existing.ProfileId = profileId;
				existing.ChannelTitle = channelInfo.Title;
				existing.CustomUrl = channelInfo.CustomUrl;
				existing.ProfilePictureUrl = base64Avatar ?? channelInfo.AvatarUrl;
				existing.AccessToken = tokens.Value.AccessToken;
				if (!string.IsNullOrEmpty(tokens.Value.RefreshToken))
				{
					existing.RefreshToken = tokens.Value.RefreshToken;
				}
				existing.TokenExpiresAt = tokenExpireDateUtc;
				existing.SubscriberCount = channelInfo.SubscriberCount;
				existing.VideoCount = channelInfo.VideoCount;
				existing.IsActive = true;
			}

			await _db.SaveChangesAsync();

			_logger.LogInformation("✅ [YouTube] Канал '{Title}' (@{Handle}) успешно подключен к профилю {ProfileId}!",
				channelInfo.Title, channelInfo.CustomUrl, profileId);

			return RedirectToAction("Index", new { botId = existing.Id });
		}

		// ==========================================================
		// 3. СТРАНИЦА УПРАВЛЕНИЯ КАНАЛОМ (/youtube)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.YouTubeSettings
				.Include(y => y.Profile)
				.FirstOrDefaultAsync(y => y.Id == botId && y.UserId == userId);

			if (settings == null) return RedirectToAction("Profile", "Auth");

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			// МЕТОД ИЗ BaseController: считаем посты для предупреждения перед удалением
			ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.YouTube, botId);

			return View(settings);
		}

		// ==========================================================
		// 4. ОТКЛЮЧЕНИЕ КАНАЛА (С УМНОЙ ОЧИСТКОЙ ПОСТОВ И ОБЛАКА)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.Id == botId && y.UserId == userId);

			if (settings != null)
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ В ПЛАНИРОВЩИКЕ И ВИДЕОФАЙЛОВ GOOGLE DRIVE:
				await CleanupLinkedPostsAsync(NetworkType.YouTube, botId);

				// 2. Удаление канала из базы данных
				_db.YouTubeSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation("✅ [YouTube] Канал '{Title}' (Id: {BotId}) и связанные посты успешно удалены.", settings.ChannelTitle, botId);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 5. СОХРАНЕНИЕ НАСТРОЕК КАНАЛА (ПРОМПТЫ / АВТООТВЕТЫ)
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(
			int botId,
			string systemPrompt,
			int profileId,
			int commentReplyMode,
			string? commentTemplates,
			string commentPrompt)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.Id == botId && y.UserId == userId);

			if (settings == null) return RedirectToAction("Profile", "Auth");

			try
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				var isCommentsRaw = Request.Form["isCommentsEnabled"].ToString();
				bool isComments = isCommentsRaw.Contains("true");

				settings.IsActive = isActive;
				settings.SystemPrompt = systemPrompt ?? "";
				settings.ProfileId = profileId;

				settings.IsCommentsEnabled = isComments;
				settings.CommentReplyMode = commentReplyMode > 0 ? commentReplyMode : 2;
				settings.CommentTemplates = commentTemplates;
				settings.CommentPrompt = !string.IsNullOrWhiteSpace(commentPrompt) ? commentPrompt : settings.CommentPrompt;

				await _db.SaveChangesAsync();
				_logger.LogInformation("✅ [YouTube] Настройки канала '{Title}' успешно сохранены. Автоответы: {Status}",
					settings.ChannelTitle, isComments ? "ВКЛ" : "ВЫКЛ");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при сохранении настроек YouTube {BotId}", botId);
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}
	}
}