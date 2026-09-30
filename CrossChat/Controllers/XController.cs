using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("x")]
	public class XController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly HttpClient _httpClient;
		private readonly SocialMediaSettings _settings;
		private readonly IDistributedCache _cache;
		private readonly ILogger<XController> _logger;
		private readonly IXService _xService;

		private string ClientId => _settings.XClientId;
		private string ClientSecret => _settings.XClientSecret;
		private string RedirectUri => $"{APP_URL}/x/auth/callback";

		public XController(
			AppDbContext db, 
			IOptions<SocialMediaSettings> options, 
			IDistributedCache cache, 
			ILogger<XController> logger, 
			IXService xService)
		{
			_db = db;
			_settings = options.Value;
			_cache = cache;
			_logger = logger;
			_xService = xService;
			_httpClient = new HttpClient();
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК (/x)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index(int? botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			XSettings? settings = null;
			if (botId.HasValue)
			{
				settings = await _db.XSettings
					.Include(p => p.Profile)
					.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

				// МЕТОД ИЗ BaseController: считаем посты для предупреждения перед отключением
				ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.X, botId.Value);
			}
			else
			{
				ViewBag.LinkedPostsCount = 0;
			}

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			return View(settings);
		}

		// ==========================================================
		// 2. СТАРТ АВТОРИЗАЦИИ (OAUTH 2.0 PKCE)
		// ==========================================================
		[HttpPost("connect")]
		public async Task<IActionResult> Connect([FromQuery] int? profileId)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
			int userId = int.Parse(userIdStr);

			// Безопасное определение профиля
			int activeProfileId = profileId ?? await GetActiveProfileIdSafeAsync(_db, userId);

			var state = Guid.NewGuid().ToString("N");
			var codeVerifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
			var codeChallenge = GenerateCodeChallenge(codeVerifier);

			var cacheOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) };
			await _cache.SetStringAsync($"x_state:{state}", state, cacheOptions);
			await _cache.SetStringAsync($"x_verifier:{state}", codeVerifier, cacheOptions);
			await _cache.SetStringAsync($"x_userId:{state}", userIdStr, cacheOptions);
			// Сохраняем ProfileId в Redis, чтобы не зависеть от кук при редиректе
			await _cache.SetStringAsync($"x_profileId:{state}", activeProfileId.ToString(), cacheOptions);

			var scopes = "tweet.read tweet.write users.read media.write offline.access";

			var url = $"https://x.com/i/oauth2/authorize?" +
					  $"response_type=code&" +
					  $"client_id={ClientId}&" +
					  $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
					  $"scope={Uri.EscapeDataString(scopes)}&" +
					  $"state={state}&" +
					  $"code_challenge={codeChallenge}&" +
					  $"code_challenge_method=S256";

			return Redirect(url);
		}

		// ==========================================================
		// 3. КОЛЛБЭК АВТОРИЗАЦИИ (CALLBACK)
		// ==========================================================
		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? state, string? error)
		{
			var verifier = await _cache.GetStringAsync($"x_verifier:{state}");
			var internalUserId = await _cache.GetStringAsync($"x_userId:{state}");
			var profileIdStr = await _cache.GetStringAsync($"x_profileId:{state}");

			if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(internalUserId))
			{
				_logger.LogWarning("[X] Сессия авторизации устарела");
				return BadRequest("Сессия авторизации истекла. Попробуйте снова.");
			}

			int userId = int.Parse(internalUserId);
			int? profileId = int.TryParse(profileIdStr, out var pid) ? pid : null;

			var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");

			var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

			var formData = new Dictionary<string, string> {
				{ "code", code! },
				{ "grant_type", "authorization_code" },
				{ "client_id", ClientId },
				{ "redirect_uri", RedirectUri },
				{ "code_verifier", verifier }
			};
			request.Content = new FormUrlEncodedContent(formData);

			var response = await _httpClient.SendAsync(request);
			var json = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError("[X] Ошибка получения токена: {Json}", json);
				return Content($"Ошибка авторизации в X: {json}");
			}

			var data = JsonDocument.Parse(json).RootElement;

			var settings = await SaveXTokenToDb(userId, data, profileId);

			return RedirectToAction("Index", new { botId = settings?.Id ?? 0 });
		}

		// ==========================================================
		// 4. ОТКЛЮЧЕНИЕ АККАУНТА (С УМНОЙ ОЧИСТКОЙ ПОСТОВ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ В ПЛАНИРОВЩИКЕ И ФАЙЛОВ GOOGLE DRIVE:
				await CleanupLinkedPostsAsync(NetworkType.X, botId);

				// 2. Удаляем сам аккаунт из БД
				_db.XSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation("✅ [X] Аккаунт @{ScreenName} (Id: {BotId}) и связанные публикации успешно удалены.", settings.ScreenName, botId);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 5. СОХРАНЕНИЕ НАСТРОЕК
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt, int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				settings.SystemPrompt = systemPrompt ?? "";
				settings.IsActive = isActive;
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
				_logger.LogInformation("✅ [X] Настройки аккаунта @{ScreenName} обновлены.", settings.ScreenName);
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		private async Task<XSettings> SaveXTokenToDb(int userId, JsonElement data, int? profileId)
		{
			var accessToken = data.GetProperty("access_token").GetString()!;
			var refreshToken = data.GetProperty("refresh_token").GetString()!;
			var expiresIn = data.GetProperty("expires_in").GetInt32();

			// ВАЖНО: Срок жизни токена сохраняем строго в UTC!
			var tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);

			string? xUserId = null;
			string? screenName = null;
			string? profilePicUrl = null;

			try
			{
				var profile = await _xService.GetXUserProfileAsync(accessToken);
				xUserId = profile.Id;
				screenName = profile.Username;
				profilePicUrl = profile.ProfilePictureUrl;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[X] Не удалось получить профиль пользователя X");
			}

			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.XUserId == xUserId);

			bool isNew = false;
			if (settings == null)
			{
				int targetProfileId = (profileId.HasValue && profileId.Value > 0)
					? profileId.Value
					: await GetActiveProfileIdSafeAsync(_db, userId);

				settings = new XSettings 
				{ 
					UserId = userId, 
					XUserId = xUserId, 
					ProfileId = targetProfileId 
				};
				_db.XSettings.Add(settings);
				isNew = true;
			}

			if (!string.IsNullOrEmpty(profilePicUrl))
			{
				var base64Avatar = await DownloadImageAsBase64ForHtml(profilePicUrl);
				if (base64Avatar != null)
				{
					settings.ProfilePictureUrl = base64Avatar;
				}
			}

			settings.AccessToken = accessToken;
			settings.RefreshToken = refreshToken;
			settings.TokenExpiresAt = tokenExpiresAtUtc;
			settings.ScreenName = screenName;
			settings.IsActive = false;

			await _db.SaveChangesAsync();

			_logger.LogInformation(isNew
				? $"[X] Добавлен новый аккаунт @{screenName} для пользователя {userId}"
				: $"[X] Обновлен токен для существующего аккаунта @{screenName}");

			return settings;
		}

		private string GenerateCodeChallenge(string verifier)
		{
			using var sha256 = SHA256.Create();
			var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
			return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
		}		
	}
}