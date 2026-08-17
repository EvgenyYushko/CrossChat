using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using static CrossChat.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;
using static CrossChat.Helpers.TimeZoneHelper;

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

		public XController(AppDbContext db, IOptions<SocialMediaSettings> options, IDistributedCache cache
			, ILogger<XController> logger, IXService xService)
		{
			_db = db;
			_settings = options.Value;
			_cache = cache;
			_logger = logger;
			_xService = xService;
			_httpClient = new HttpClient();
		}

		[HttpGet]
		public async Task<IActionResult> Index(int? botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = botId.HasValue
				? await _db.XSettings.Include(p => p.Profile).FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId)
				: null;

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			return View(settings);
		}

		[HttpPost("connect")]
		public async Task<IActionResult> Connect()
		{
			var state = Guid.NewGuid().ToString("N");
			// Code Verifier — случайная строка от 43 до 128 символов
			var codeVerifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
			var codeChallenge = GenerateCodeChallenge(codeVerifier);

			// Сохраняем в кеш (Redis), чтобы проверить в Callback
			var cacheOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) };
			await _cache.SetStringAsync($"x_state:{state}", state, cacheOptions);
			await _cache.SetStringAsync($"x_verifier:{state}", codeVerifier, cacheOptions);
			await _cache.SetStringAsync($"x_userId:{state}", User.FindFirstValue(ClaimTypes.NameIdentifier)!, cacheOptions);

			// tweet.read - чтобы видеть свои посты
			// tweet.write - чтобы бот мог постить
			// users.read - чтобы получить имя и аватарку профиля
			// offline.access - ЧТОБЫ ПОЛУЧИТЬ REFRESH TOKEN (Обязательно!)
			var scopes = "tweet.read tweet.write users.read media.write offline.access";

			var url = $"https://x.com/i/oauth2/authorize?" +
					  $"response_type=code&" +
					  $"client_id={ClientId}&" + // Твой ID со скрина
					  $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
					  $"scope={Uri.EscapeDataString(scopes)}&" +
					  $"state={state}&" +
					  $"code_challenge={codeChallenge}&" +
					  $"code_challenge_method=S256";

			return Redirect(url);
		}

		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? state, string? error)
		{
			// 1. Достаем данные из кеша по state
			var verifier = await _cache.GetStringAsync($"x_verifier:{state}");
			var internalUserId = await _cache.GetStringAsync($"x_userId:{state}");

			if (string.IsNullOrEmpty(verifier)) return BadRequest("Сессия истекла");

			// 2. Формируем запрос к X
			var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitter.com/2/oauth2/token");

			// Авторизация приложения (Basic Auth)
			var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

			var formData = new Dictionary<string, string> {
				{ "code", code },
				{ "grant_type", "authorization_code" },
				{ "client_id", ClientId },
				{ "redirect_uri", RedirectUri },
				{ "code_verifier", verifier }
			};
			request.Content = new FormUrlEncodedContent(formData);

			// 3. Получаем токены
			var response = await _httpClient.SendAsync(request);
			var json = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode) return Content($"Ошибка X: {json}");

			var data = JsonDocument.Parse(json).RootElement;

			// 4. Сохраняем в БД (AccessToken, RefreshToken, ExpiresIn)
			var settings = await SaveXTokenToDb(int.Parse(internalUserId), data);

			return RedirectToAction("Index", new { botId = settings?.Id ?? 0 });
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			// 1. Получаем ID текущего пользователя
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// 2. Ищем конкретную настройку X, принадлежащую этому пользователю
			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 3. Удаляем запись из базы
				_db.XSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation($"[X] Пользователь {userId} отключил аккаунт @{settings.ScreenName}");
			}

			return RedirectToAction("Index");
		}

		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt, int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// Безопасно считываем состояние тумблера из формы
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				settings.SystemPrompt = systemPrompt ?? "";
				settings.IsActive = isActive;
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
				_logger.LogInformation($"[X] Настройки аккаунта @{settings.ScreenName} обновлены. Активность: {isActive}");
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		private async Task<XSettings> SaveXTokenToDb(int userId, JsonElement data)
		{
			// 1. Извлекаем данные о токенах из ответа X
			var accessToken = data.GetProperty("access_token").GetString();
			var refreshToken = data.GetProperty("refresh_token").GetString();
			var expiresIn = data.GetProperty("expires_in").GetInt32();
			var tokenExpiresAt = DateTimeNow.AddSeconds(expiresIn);

			// 2. Получаем данные профиля пользователя из X API v2
			string? xUserId = null;
			string? screenName = null;
			string? profilePicUrl = null;

			try
			{
				var profile =  await _xService.GetXUserProfileAsync(accessToken);
				xUserId = profile.Id;
				screenName = profile.Username;
				profilePicUrl = profile.ProfilePictureUrl;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[X] Не удалось получить данные профиля после авторизации");
			}

			// --- ГЛАВНОЕ ИСПРАВЛЕНИЕ: Поиск по паре UserId + XUserId ---
			// Это позволит одному пользователю иметь несколько аккаунтов X в системе
			var settings = await _db.XSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.XUserId == xUserId);

			bool isNew = false;
			if (settings == null)
			{
				settings = new XSettings { UserId = userId, XUserId = xUserId, ProfileId = GetActiveProfileId().Value };
				_db.XSettings.Add(settings);
				isNew = true;
			}

			// 3. Скачиваем аватарку в Base64 (как и в других соцсетях)
			if (!string.IsNullOrEmpty(profilePicUrl))
			{
				// Twitter часто присылает маленькие картинки _normal. 
				// Если хочешь побольше, можно заменить: profilePicUrl = profilePicUrl.Replace("_normal", "_400x400");
				var base64Avatar = await DownloadImageAsBase64ForHtml(profilePicUrl);
				if (base64Avatar != null)
				{
					settings.ProfilePictureUrl = base64Avatar;
				}
			}

			// 4. Обновляем поля
			settings.AccessToken = accessToken;
			settings.RefreshToken = refreshToken;
			settings.TokenExpiresAt = tokenExpiresAt;
			settings.ScreenName = screenName;
			settings.IsActive = false;

			await _db.SaveChangesAsync();

			_logger.LogInformation(isNew
				? $"[X] Добавлен новый аккаунт @{screenName} для пользователя {userId}"
				: $"[X] Обновлен токен для существующего аккаунта @{screenName}");

			return settings;
		}

		// Вспомогательный метод для PKCE (такой же как в BlueSky)
		private string GenerateCodeChallenge(string verifier)
		{
			using var sha256 = SHA256.Create();
			var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
			return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
		}		
	}
}
