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

		public FaceBookController(
			ILogger<FaceBookController> logger,
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
			if (mode == "subscribe" && token == "Test")
			{
				_logger.LogInformation("[Facebook Webhook] Вебхук успешно верифицирован.");
				return Ok(challenge);
			}

			_logger.LogWarning("[Facebook Webhook] Ошибка верификации вебхука.");
			return Forbid();
		}

		[AllowAnonymous]
		[HttpPost("webhook")]
		public async Task<IActionResult> ReceiveWebhook()
		{
			try
			{
				using var reader = new StreamReader(Request.Body);
				var body = await reader.ReadToEndAsync();
				_logger.LogInformation("[Facebook Webhook]: " + body);
				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook Webhook] Ошибка обработки входящего вебхука");
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

			// Полный проверенный набор разрешений для публикации, сообщений и бизнес-страниц
			var fbScopes = "pages_manage_posts,pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,pages_read_user_content,business_management,public_profile,email";
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?client_id={AppId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={fbScopes}&response_type=code&auth_type=rerequest";

			return View(settings);
		}

		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? error, string? error_description)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
			{
				_logger.LogWarning($"[Facebook] Ошибка авторизации: {error_description}");
				return RedirectToAction("Index");
			}

			try
			{
				// 1. Получаем Short-Lived User Token (2 часа)
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				var shortResp = await _httpClient.GetFromJsonAsync<JsonElement>(shortTokenUrl);
				var shortUserToken = shortResp.GetProperty("access_token").GetString();

				// 2. Обмениваем на 60-дневный Long-Lived User Token
				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortUserToken}";

				var longResp = await _httpClient.GetFromJsonAsync<JsonElement>(longTokenUrl);
				var longUserToken = longResp.GetProperty("access_token").GetString();

				// 3. Динамически запрашиваем ВСЕ страницы пользователя (личные и Meta Business Suite)
				var accountsUrl = $"https://graph.facebook.com/v22.0/me/accounts?fields=name,id,access_token,picture{{url}}&access_token={longUserToken}";
				var accountsResp = await _httpClient.GetFromJsonAsync<JsonElement>(accountsUrl);
				
				var pagesList = new List<JsonElement>();

				if (accountsResp.TryGetProperty("data", out var pagesData))
				{
					foreach (var page in pagesData.EnumerateArray())
					{
						pagesList.Add(page);
					}
				}

				// Если в me/accounts пусто, автоматически опрашиваем Meta Business Suite
				if (pagesList.Count == 0)
				{
					try
					{
						var bizUrl = $"https://graph.facebook.com/v22.0/me/businesses?fields=id,name,owned_pages{{id,name,access_token,picture{{url}}}},client_pages{{id,name,access_token,picture{{url}}}}&access_token={longUserToken}";
						var bizResp = await _httpClient.GetFromJsonAsync<JsonElement>(bizUrl);

						if (bizResp.TryGetProperty("data", out var bizData))
						{
							foreach (var b in bizData.EnumerateArray())
							{
								if (b.TryGetProperty("owned_pages", out var owned) && owned.TryGetProperty("data", out var ownedData))
								{
									foreach (var page in ownedData.EnumerateArray())
									{
										var pid = page.GetProperty("id").GetString();
										if (!pagesList.Any(p => p.GetProperty("id").GetString() == pid)) pagesList.Add(page);
									}
								}
								if (b.TryGetProperty("client_pages", out var client) && client.TryGetProperty("data", out var clientData))
								{
									foreach (var page in clientData.EnumerateArray())
									{
										var pid = page.GetProperty("id").GetString();
										if (!pagesList.Any(p => p.GetProperty("id").GetString() == pid)) pagesList.Add(page);
									}
								}
							}
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "[Facebook] Ошибка при чтении me/businesses");
					}
				}

				if (pagesList.Count == 0)
				{
					_logger.LogWarning("[Facebook] У пользователя не найдено доступных страниц.");
					return RedirectToAction("Index");
				}

				// 4. Проверяем авторизацию пользователя на нашем сайте
				var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
				if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
				var userId = int.Parse(userIdStr);

				// 5. Сохраняем все найденные страницы
				List<FacebookSettings> settings = new();
				foreach (var page in pagesList)
				{
					settings.Add(await SaveFacebookPage(userId, page));
				}

				_logger.LogInformation($"[Facebook] Успешно подключено страниц: {settings.Count} для пользователя {userId}");

				// Переходим на страницу первой подключенной страницы
				var firstPageId = settings.FirstOrDefault()?.Id;
				return firstPageId.HasValue
					? RedirectToAction("Index", new { botId = firstPageId.Value })
					: RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook] Ошибка при обработке Callback");
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
			var pageId = pageData.GetProperty("id").GetString()!;
			var pageName = pageData.GetProperty("name").GetString()!;
			var pageToken = pageData.GetProperty("access_token").GetString()!;
			
			string? pictureUrl = null;
			if (pageData.TryGetProperty("picture", out var picProp) && 
			    picProp.TryGetProperty("data", out var dataProp) &&
			    dataProp.TryGetProperty("url", out var urlProp))
			{
				pictureUrl = urlProp.GetString();
			}

			// 1. Подписываем страницу на вебхуки Messenger (для автоответов)
			try
			{
				var subscribeUrl = $"https://graph.facebook.com/v22.0/{pageId}/subscribed_apps?subscribed_fields=messages,messaging_postbacks&access_token={pageToken}";
				await _httpClient.PostAsync(subscribeUrl, null);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, $"[Facebook] Не удалось подписать страницу {pageName} на вебхуки Messenger");
			}

			// 2. Ищем или создаем запись в БД
			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.PageId == pageId);

			if (settings == null)
			{
				settings = new FacebookSettings 
				{ 
					UserId = userId, 
					PageId = pageId, 
					ProfileId = GetActiveProfileId().Value 
				};
				_db.FacebookSettings.Add(settings);
			}

			if (!string.IsNullOrEmpty(pictureUrl))
			{
				settings.ProfilePictureUrl = await DownloadImageAsBase64ForHtml(pictureUrl);
			}

			settings.PageName = pageName;
			settings.PageAccessToken = pageToken;
			settings.IsActive = true;

			await _db.SaveChangesAsync();
			_logger.LogInformation($"[Facebook] Страница '{pageName}' ({pageId}) сохранена в БД для пользователя {userId}");

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
