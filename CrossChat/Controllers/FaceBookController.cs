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

			// Если botId передан, загружаем конкретную страницу
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

			// Ссылка на авторизацию (если нужно подключить новую страницу)
			var fbScopes = "pages_manage_posts,pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,public_profile,email";
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v21.0/dialog/oauth?client_id={_settings.AppId}&redirect_uri={Url.Action("Callback", "Facebook", null, Request.Scheme)}&scope={fbScopes}&response_type=code";

			return View(settings);
		}

		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				_logger.LogWarning($"FB Auth Error: {error_description}");
				return RedirectToAction("Index");
			}

			try
			{
				// STEP 1: Получаем Short-Lived User Token (2 часа)
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				var shortResp = await _httpClient.GetFromJsonAsync<JsonElement>(shortTokenUrl);
				var shortUserToken = shortResp.GetProperty("access_token").GetString();

				// STEP 2: Обмениваем на Long-Lived User Token (60 дней)
				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortUserToken}";

				var longResp = await _httpClient.GetFromJsonAsync<JsonElement>(longTokenUrl);
				var longUserToken = longResp.GetProperty("access_token").GetString();

				// STEP 3: Получаем список СТРАНИЦ и их бессрочные токены
				var accountsUrl = $"https://graph.facebook.com/v22.0/me/accounts?fields=name,id,access_token,picture{{url}}&access_token={longUserToken}";
				var accountsResp = await _httpClient.GetFromJsonAsync<JsonElement>(accountsUrl);
				var pages = accountsResp.GetProperty("data");

				// Для сохранения нам нужен внутренний UserId, но в Callback мы анонимны. 
				// ВАЖНО: Ты можешь использовать state для передачи UserId или убедиться, что кука жива.
				// Если ты используешь мой прошлый фикс с Redis для BlueSky, примени его и здесь.
				var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
				if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
				var userId = int.Parse(userIdStr);

				foreach (var page in pages.EnumerateArray())
				{
					await SaveFacebookPage(userId, page);
				}

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Facebook Auth Process Error");
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

		private async Task SaveFacebookPage(int userId, JsonElement pageData)
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
			settings.ProfilePictureUrl = await DownloadImageAsBase64(pictureUrl);
			settings.PageName = pageName;
			settings.PageAccessToken = pageToken; // Это уже Long-Lived Page Token
			settings.IsActive = true;

			await _db.SaveChangesAsync();
			_logger.LogInformation($"Facebook Page {pageName} ({pageId}) saved for User {userId}");
		}

		private async Task<string?> DownloadImageAsBase64(string imageUrl)
		{
			if (string.IsNullOrEmpty(imageUrl)) return null;

			try
			{
				// Используем _httpClient, который уже есть в контроллере, или создаем новый для чистых заголовков
				using var client = new HttpClient();

				// Притворяемся браузером, чтобы CDN не блочил
				client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

				var imageBytes = await client.GetByteArrayAsync(imageUrl);
				var base64String = Convert.ToBase64String(imageBytes);

				// ВАЖНО: Возвращаем сразу готовый для HTML формат!
				// Тогда во View ничего менять не придется.
				return $"data:image/jpeg;base64,{base64String}";
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Error downloading profile image from {imageUrl}");
				return null; // Если не вышло скачать - будет без аватарки
			}
		}
	}
}
