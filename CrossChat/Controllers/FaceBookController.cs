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
	public class FaceBookController : Controller
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

		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Загружаем настройки, чтобы передать их во View
			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			var fbScopes = "pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,public_profile,email";
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?" +
							  $"client_id={AppId}&" + // Здесь ID Facebook приложения
							  $"redirect_uri={RedirectUri}&" +
							  $"response_type=code&" +
							  $"auth_type=reauthenticate&" +
							  $"scope={fbScopes}";

			if(settings is null)
			{
				return View(new List<FacebookSettings>());
			}

			return View(new List<FacebookSettings>(){settings});
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
				settings = new FacebookSettings { UserId = userId, PageId = pageId };
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
