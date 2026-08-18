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

			var fbScopes = "pages_manage_posts,pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,pages_read_user_content,business_management,public_profile,email";

			// ИСПРАВЛЕНИЕ: Используем RedirectUri напрямую, чтобы он на 100% совпадал с Callback!
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?client_id={AppId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={fbScopes}&response_type=code&auth_type=rerequest";

			return View(settings);
		}

		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
		{
			_logger.LogInformation("=================================================================");
			_logger.LogInformation("🚀 [Facebook Callback] СТАРТ ПОЛНОГО ДИАГНОСТИЧЕСКОГО ЛОГИРОВАНИЯ");
			_logger.LogInformation("=================================================================");

			if (!string.IsNullOrEmpty(error))
			{
				_logger.LogError("❌ [Facebook Callback] Ошибка: {Error}", error);
				_logger.LogError("❌ [Facebook Callback] Описание: {Desc}", error_description);
				return RedirectToAction("Index");
			}

			if (string.IsNullOrEmpty(code))
			{
				_logger.LogError("❌ [Facebook Callback] Параметр code пустой!");
				return RedirectToAction("Index");
			}

			// 0. ВЫВОДИМ ПОЛНЫЙ КОД АВТОРИЗАЦИИ
			_logger.LogInformation("🔑 [0. ПОЛНЫЙ CODE АВТОРИЗАЦИИ]:\n{Code}", code);

			try
			{
				// -------------------------------------------------------------
				// STEP 1: Запрос Short-Lived User Token (2 часа)
				// -------------------------------------------------------------
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				_logger.LogInformation("📡 [1. URL ЗАПРОСА SHORT TOKEN]:\n{Url}", shortTokenUrl);

				using var shortReq = await _httpClient.GetAsync(shortTokenUrl);
				var shortJson = await shortReq.Content.ReadAsStringAsync();

				_logger.LogInformation("📦 [1. СЫРОЙ ОТВЕТ SHORT TOKEN]:\n{Json}", shortJson);

				if (!shortReq.IsSuccessStatusCode)
				{
					_logger.LogError("❌ Ошибка на Шаге 1 (HTTP {Status})", shortReq.StatusCode);
					return RedirectToAction("Index");
				}

				using var shortDoc = JsonDocument.Parse(shortJson);
				var shortUserToken = shortDoc.RootElement.GetProperty("access_token").GetString()!;

				_logger.LogInformation("🟢 [1. ПОЛНЫЙ SHORT USER TOKEN (2 ЧАСА)]:\n{Token}", shortUserToken);

				// -------------------------------------------------------------
				// STEP 2: Обмен на Long-Lived User Token (60 дней)
				// -------------------------------------------------------------
				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortUserToken}";

				_logger.LogInformation("📡 [2. URL ОБМЕНА LONG TOKEN]:\n{Url}", longTokenUrl);

				using var longReq = await _httpClient.GetAsync(longTokenUrl);
				var longJson = await longReq.Content.ReadAsStringAsync();

				_logger.LogInformation("📦 [2. СЫРОЙ ОТВЕТ LONG TOKEN]:\n{Json}", longJson);

				if (!longReq.IsSuccessStatusCode)
				{
					_logger.LogError("❌ Ошибка на Шаге 2 (HTTP {Status})", longReq.StatusCode);
					return RedirectToAction("Index");
				}

				using var longDoc = JsonDocument.Parse(longJson);
				var longUserToken = longDoc.RootElement.GetProperty("access_token").GetString()!;

				_logger.LogInformation("🟢 [2. ПОЛНЫЙ 60-ДНЕВНЫЙ USER TOKEN]:\n{Token}", longUserToken);
				_logger.LogInformation("🔍 [ПРОВЕРИТЬ ТОКЕН В META DEBUGGER]:\nhttps://developers.facebook.com/tools/debug/accesstoken/?access_token={Token}", longUserToken);

				// -------------------------------------------------------------
				// STEP 3: Запрос списка страниц и реальных разрешений
				// -------------------------------------------------------------

				// 3.1. Проверяем выданные права токена через /me/permissions
				var permUrl = $"https://graph.facebook.com/v22.0/me/permissions?access_token={longUserToken}";
				using var permReq = await _httpClient.GetAsync(permUrl);
				var permJson = await permReq.Content.ReadAsStringAsync();
				_logger.LogInformation("🛡️ [3.1 РЕАЛЬНЫЕ ВЫДАННЫЕ ПРАВА /me/permissions]:\n{Json}", permJson);

				// 3.2. Запрос страниц через /me/accounts
				var accountsUrl = $"https://graph.facebook.com/v22.0/me/accounts?fields=name,id,access_token,tasks,picture{{url}}&access_token={longUserToken}";
				_logger.LogInformation("📡 [3.2 URL ЗАПРОСА СТРАНИЦ]:\n{Url}", accountsUrl);

				using var accountsReq = await _httpClient.GetAsync(accountsUrl);
				var accountsJson = await accountsReq.Content.ReadAsStringAsync();

				_logger.LogInformation("📦 [3.2 СЫРОЙ ОТВЕТ СТРАНИЦ /me/accounts]:\n{Json}", accountsJson);

				// -------------------------------------------------------------
				// STEP 4: Разбор и вывод токенов конкретных страниц
				// -------------------------------------------------------------
				using var accountsDoc = JsonDocument.Parse(accountsJson);
				var pages = accountsDoc.RootElement.GetProperty("data");
				int pagesCount = pages.GetArrayLength();

				_logger.LogInformation("📄 Найдено страниц: {Count}", pagesCount);

				var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
				_logger.LogInformation("👤 Авторизованный UserId на сайте: {UserId}", userIdStr ?? "NULL");

				if (string.IsNullOrEmpty(userIdStr))
				{
					_logger.LogError("❌ Сбой: кука авторизации сайта отсутствует (User is Anonymous).");
					return Unauthorized();
				}

				var userId = int.Parse(userIdStr);
				List<FacebookSettings> settings = new();

				foreach (var page in pages.EnumerateArray())
				{
					var pageName = page.GetProperty("name").GetString();
					var pageId = page.GetProperty("id").GetString();
					var pageToken = page.GetProperty("access_token").GetString();

					_logger.LogInformation("-----------------------------------------------------------------");
					_logger.LogInformation("📄 СТРАНИЦА: '{Name}' (ID: {Id})", pageName, pageId);
					_logger.LogInformation("🔑 ПОЛНЫЙ PAGE ACCESS TOKEN СТРАНИЦЫ:\n{PageToken}", pageToken);
					_logger.LogInformation("-----------------------------------------------------------------");

					var savedSetting = await SaveFacebookPage(userId, page);
					settings.Add(savedSetting);
				}

				var firstPageId = settings.FirstOrDefault()?.Id;
				_logger.LogInformation("🏁 Успешное завершение. Редирект на botId = {BotId}", firstPageId);
				_logger.LogInformation("=================================================================");

				return firstPageId.HasValue
					? RedirectToAction("Index", new { botId = firstPageId.Value })
					: RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ КРИТИЧЕСКОЕ ИСКЛЮЧЕНИЕ В CALLBACK");
				_logger.LogInformation("=================================================================");
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
