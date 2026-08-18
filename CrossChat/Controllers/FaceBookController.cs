using System.Security.Claims;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Worker.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static CrossChat.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("facebook")]
	public class FaceBookController : BaseController
	{
		private readonly ILogger<FaceBookController> _logger;
		private readonly SocialMediaSettings _settings;
		private readonly HttpClient _httpClient;
		private readonly AppDbContext _db;
		private string RedirectUri => $"{APP_URL}/facebook/auth/callback";

		public FaceBookController(ILogger<FaceBookController> logger,
			IOptions<SocialMediaSettings> options,
			AppDbContext db)
		{
			_logger = logger;
			_settings = options.Value;
			_db = db;
			_httpClient = new HttpClient();
		}

		private string AppId => _settings.AppId;
		private string AppSecret => _settings.AppSecret;

		[AllowAnonymous]
		[HttpGet("webhook")]
		public IActionResult VerifyWebhook(
			[FromQuery(Name = "hub.mode")] string mode,
			[FromQuery(Name = "hub.verify_token")] string token,
			[FromQuery(Name = "hub.challenge")] string challenge)
		{
			_logger.LogInformation($"Webhook verification: mode={mode}, token={token}");

			// Проверяем токен верификации
			if (mode == "subscribe" && token == "Test")
			{
				_logger.LogInformation("Webhook verified successfully");
				return Ok(challenge);
			}
			else
			{
				_logger.LogWarning("Webhook verification failed");
				return Forbid();
			}
		}

		[AllowAnonymous]
		[HttpPost("webhook")]
		public async Task<IActionResult> ReceiveWebhook()
		{
			try
			{
				using var reader = new StreamReader(Request.Body);
				var body = await reader.ReadToEndAsync();

				_logger.LogInformation(body);

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing Instagram webhook");
				return StatusCode(500);
			}
		}

		[HttpGet]
		public async Task<IActionResult> Index(int? botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			FacebookSettings? settings = null;
			if (botId.HasValue)
			{
				settings = await _db.FacebookSettings
					.Include(p => p.Profile)
					.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);
			}

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			var fbScopes = "pages_manage_posts,pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,pages_read_user_content,public_profile,email";

			// ИСПРАВЛЕНИЕ: Используем RedirectUri напрямую, чтобы он на 100% совпадал с Callback!
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?client_id={AppId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={fbScopes}&response_type=code&auth_type=rerequest";

			return View(settings);
		}

		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
		{
			_logger.LogInformation("=== [Facebook Callback] СТАРТ ОБРАБОТКИ АВТОРИЗАЦИИ ===");

			// 1. Проверяем ошибки от Facebook
			if (!string.IsNullOrEmpty(error))
			{
				_logger.LogError("❌ [Facebook Callback] Ошибка авторизации от Facebook: {Error} - {Description}", error, error_description);
				return RedirectToAction("Index");
			}

			if (string.IsNullOrEmpty(code))
			{
				_logger.LogError("❌ [Facebook Callback] Код авторизации (code) пуст или отсутствует в запросе!");
				return RedirectToAction("Index");
			}

			_logger.LogInformation("🔑 [Facebook Callback] Код авторизации получен: {CodePrefix}...", code.Substring(0, Math.Min(15, code.Length)));

			try
			{
				// STEP 1: Получаем Short-Lived User Token (2 часа)
				_logger.LogInformation("⏳ [Facebook Callback] ШАГ 1: Запрос Short-Lived User Token...");
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				using var shortReq = await _httpClient.GetAsync(shortTokenUrl);
				var shortJson = await shortReq.Content.ReadAsStringAsync();

				if (!shortReq.IsSuccessStatusCode)
				{
					_logger.LogError("❌ [Facebook Callback] Ошибка получения Short Token (HTTP {StatusCode}): {Response}", shortReq.StatusCode, shortJson);
					return RedirectToAction("Index");
				}

				using var shortDoc = JsonDocument.Parse(shortJson);
				var shortUserToken = shortDoc.RootElement.GetProperty("access_token").GetString()!;
				_logger.LogInformation("✅ [Facebook Callback] Short Token успешно получен!");

				// STEP 2: Обмениваем на Long-Lived User Token (60 дней)
				_logger.LogInformation("⏳ [Facebook Callback] ШАГ 2: Обмен на 60-дневный Long-Lived User Token...");
				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortUserToken}";

				using var longReq = await _httpClient.GetAsync(longTokenUrl);
				var longJson = await longReq.Content.ReadAsStringAsync();

				if (!longReq.IsSuccessStatusCode)
				{
					_logger.LogError("❌ [Facebook Callback] Ошибка обмена на Long Token (HTTP {StatusCode}): {Response}", longReq.StatusCode, longJson);
					return RedirectToAction("Index");
				}

				using var longDoc = JsonDocument.Parse(longJson);
				var longUserToken = longDoc.RootElement.GetProperty("access_token").GetString()!;
				_logger.LogInformation("✅ [Facebook Callback] Long Token получен!");

				// STEP 3: Получаем список СТРАНИЦ и их бессрочные токены
				_logger.LogInformation("⏳ [Facebook Callback] ШАГ 3: Запрос списка страниц через /me/accounts...");
				var accountsUrl = $"https://graph.facebook.com/v22.0/me/accounts?fields=name,id,access_token,picture{{url}}&access_token={longUserToken}";

				using var accountsReq = await _httpClient.GetAsync(accountsUrl);
				var accountsJson = await accountsReq.Content.ReadAsStringAsync();

				if (!accountsReq.IsSuccessStatusCode)
				{
					_logger.LogError("❌ [Facebook Callback] Ошибка получения страниц (HTTP {StatusCode}): {Response}", accountsReq.StatusCode, accountsJson);
					return RedirectToAction("Index");
				}

				using var accountsDoc = JsonDocument.Parse(accountsJson);
				var pages = accountsDoc.RootElement.GetProperty("data");
				int pagesCount = pages.GetArrayLength();

				_logger.LogInformation("📄 [Facebook Callback] Найдено страниц для подключения: {Count}", pagesCount);

				if (pagesCount == 0)
				{
					_logger.LogWarning("⚠️ [Facebook Callback] В этом аккаунте нет доступных страниц Facebook для подключения.");
					return RedirectToAction("Index");
				}

				// STEP 4: Проверяем авторизацию пользователя на сайте
				var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
				_logger.LogInformation("👤 [Facebook Callback] Проверка авторизации на сайте: UserId = {UserId}", userIdStr ?? "NULL (Сессия не передалась!)");

				if (string.IsNullOrEmpty(userIdStr))
				{
					_logger.LogError("❌ [Facebook Callback] Сбой: Кука авторизации потеряна при редиректе с Facebook! Возврат 401.");
					return Unauthorized();
				}

				var userId = int.Parse(userIdStr);
				List<FacebookSettings> settings = new();

				// STEP 5: Сохраняем страницы в БД
				foreach (var page in pages.EnumerateArray())
				{
					var pageName = page.GetProperty("name").GetString();
					var pageId = page.GetProperty("id").GetString();
					_logger.LogInformation("💾 [Facebook Callback] Сохранение страницы: '{PageName}' (ID: {PageId})...", pageName, pageId);

					var savedSetting = await SaveFacebookPage(userId, page);
					settings.Add(savedSetting);
				}

				var firstPageId = settings.FirstOrDefault()?.Id;
				_logger.LogInformation("🏁 [Facebook Callback] УСПЕХ! Страницы сохранены. Редирект на botId = {BotId}", firstPageId);

				return firstPageId.HasValue
					? RedirectToAction("Index", new { botId = firstPageId.Value })
					: RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [Facebook Callback] Критическое исключение в процессе авторизации");
				return RedirectToAction("Index");
			}
		}

		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt, int profileId)
		{
			// 1. Получаем ID текущего пользователя
			var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdClaim)) return Unauthorized();
			var userId = int.Parse(userIdClaim);

			// 2. Корректно считываем чекбокс isActive из формы
			// (учитываем хак с hidden полем: при вкл придет "false,true", при выкл - "false")
			var isActiveRaw = Request.Form["isActive"].ToString();
			bool isActive = isActiveRaw.Contains("true");

			// 3. Ищем настройки конкретной страницы в БД, проверяя владельца
			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null)
			{
				_logger.LogWarning($"[Facebook] Настройки бота {botId} не найдены для пользователя {userId}");
				return RedirectToAction("Index");
			}

			try
			{
				// 4. Обновляем данные
				settings.SystemPrompt = systemPrompt;
				settings.IsActive = isActive;
				settings.ProfileId = profileId;

				// ВАЖНО: В Facebook Pages вебхуки обычно настраиваются один раз на всё приложение
				// в панели разработчика. Поэтому здесь мы просто меняем флаг IsActive в нашей БД.
				// Наш WebhookController будет просто игнорировать запросы, если IsActive == false.

				await _db.SaveChangesAsync();
				_logger.LogInformation($"[Facebook] Настройки страницы '{settings.PageName}' обновлены. Активен: {isActive}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Facebook] Ошибка при сохранении настроек для бота {botId}");
			}

			// Возвращаемся на ту же страницу настроек с параметром botId и уведомлением
			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		private async Task<FacebookSettings> SaveFacebookPage(int userId, JsonElement pageData)
		{
			var pageId = pageData.GetProperty("id").GetString();
			var pageName = pageData.GetProperty("name").GetString();
			var pageToken = pageData.GetProperty("access_token").GetString();
			var pictureUrl = pageData.GetProperty("picture").GetProperty("data").GetProperty("url").GetString();

			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.PageId == pageId);

			if (settings == null)
			{
				settings = new FacebookSettings { UserId = userId, PageId = pageId, ProfileId = GetActiveProfileId().Value };
				_db.FacebookSettings.Add(settings);
			}

			// Скачиваем аватарку в Base64 (как в Инсте)
			settings.ProfilePictureUrl = await DownloadImageAsBase64ForHtml(pictureUrl);
			settings.PageName = pageName;
			settings.PageAccessToken = pageToken; // Это уже Long-Lived Page Token
			settings.IsActive = true;

			await _db.SaveChangesAsync();
			_logger.LogInformation($"Facebook Page {pageName} ({pageId}) saved for User {userId}");

			return settings;
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			// 1. Получаем ID текущего авторизованного пользователя
			var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userIdClaim)) return Unauthorized();
			var userId = int.Parse(userIdClaim);

			// 2. Ищем настройки конкретной страницы Facebook в БД, проверяя владельца
			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null)
			{
				_logger.LogWarning($"[Facebook] Попытка удаления ненайденной или чужой страницы {botId} пользователем {userId}");
				return RedirectToAction("Index");
			}

			try
			{
				// 3. Очищаем запланированные публикации в NetworkStates для этого бота,
				// чтобы фоновая джоба (PostPublishingJob) не пыталась слать посты на удаленный аккаунт
				int facebookNetTypeId = (int)CrossChat.Integrations.Enums.NetworkType.Facebook;
				var orphanStates = await _db.NetworkStates
					.Where(ns => ns.NetworkType == facebookNetTypeId && ns.BotId == botId)
					.ToListAsync();

				if (orphanStates.Any())
				{
					_db.NetworkStates.RemoveRange(orphanStates);
				}

				// 4. Удаляем саму интеграцию из базы данных
				_db.FacebookSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation($"[Facebook] Страница '{settings.PageName}' (BotId: {botId}) успешно отключена пользователем {userId}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Facebook] Ошибка при отключении страницы {botId} пользователя {userId}");
			}

			// Возвращаемся на главную страницу управления Facebook без выбранного botId
			return RedirectToAction("Index");
		}
	}
}
