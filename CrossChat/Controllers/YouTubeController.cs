using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static CrossChat.Helpers.TimeZoneHelper;
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

		// 1. Старт авторизации
		[HttpGet("connect")]
		public IActionResult Connect([FromQuery] int? profileId)
		{
			int activeProfileId = profileId ?? GetActiveProfileId() ?? 0;
			if (activeProfileId == 0) return RedirectToAction("Profile", "Auth");

			// Передаем ProfileId в state, чтобы после возврата из Google знать, к какому профилю привязать
			string state = activeProfileId.ToString();
			string authUrl = _youTubeService.GetAuthorizationUrl(state, GetRedirectUri());

			return Redirect(authUrl);
		}

		// 2. Коллбэк от Google
		[HttpGet("callback")]
		public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				_logger.LogWarning("[YouTube] Отказ в доступе или ошибка авторизации: {Err}", error);
				return RedirectToAction("Profile", "Auth");
			}

			if (!int.TryParse(state, out int profileId))
			{
				profileId = GetActiveProfileId() ?? 0;
			}

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Обмениваем code на токены
			var tokens = await _youTubeService.ExchangeCodeForTokensAsync(code, GetRedirectUri());
			if (tokens == null)
			{
				TempData["Error"] = "Не удалось получить токены от Google";
				return RedirectToAction("Profile", "Auth");
			}

			// Запрашиваем информацию о выбранном YouTube канале
			var channelInfo = await _youTubeService.GetChannelInfoAsync(tokens.Value.AccessToken);
			if (channelInfo == null)
			{
				TempData["Error"] = "Не найден YouTube канал в данном Google-аккаунте.";
				return RedirectToAction("Profile", "Auth");
			}

			// Ищем, подключен ли уже этот канал
			var existing = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.ChannelId == channelInfo.ChannelId && y.UserId == userId);

			string? base64Avatar = null;
			if (!string.IsNullOrEmpty(channelInfo.AvatarUrl))
			{
				base64Avatar = await DownloadImageAsBase64ForHtml(channelInfo.AvatarUrl);
			}

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
					TokenExpiresAt = DateTimeNow.AddSeconds(tokens.Value.ExpiresIn),
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
				existing.TokenExpiresAt = DateTimeNow.AddSeconds(tokens.Value.ExpiresIn);
				existing.SubscriberCount = channelInfo.SubscriberCount;
				existing.VideoCount = channelInfo.VideoCount;
				existing.IsActive = true;
			}

			await _db.SaveChangesAsync();

			_logger.LogInformation("✅ [YouTube] Канал '{Title}' (@{Handle}) успешно подключен к профилю {ProfileId}!",
				channelInfo.Title, channelInfo.CustomUrl, profileId);

			return RedirectToAction("Index", new { botId = existing.Id });
		}

		// 3. Страница управления каналом
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

			return View(settings);
		}

		// 4. Отключение канала
		[HttpPost("disconnect")]
		public async Task<IActionResult> Disconnect(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.Id == botId && y.UserId == userId);

			if (settings != null)
			{
				_db.YouTubeSettings.Remove(settings);
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Profile", "Auth");
		}

		// 5. Сохранение настроек канала
		[HttpPost("update-settings")]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt, int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.YouTubeSettings
				.FirstOrDefaultAsync(y => y.Id == botId && y.UserId == userId);

			if (settings == null) return RedirectToAction("Profile", "Auth");

			try
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				settings.IsActive = isActive;
				settings.SystemPrompt = systemPrompt ?? "";
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
				_logger.LogInformation("✅ [YouTube] Настройки канала '{Title}' обновлены.", settings.ChannelTitle);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при сохранении настроек YouTube {BotId}", botId);
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}
	}
}