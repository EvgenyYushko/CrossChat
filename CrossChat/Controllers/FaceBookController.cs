using System.Security.Claims;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Models;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("facebook")]
	public class FaceBookController : BaseController
	{
		private readonly ILogger<FaceBookController> _logger;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly SocialMediaSettings _settings;
		private readonly HttpClient _httpClient;
		private readonly AppDbContext _db;
		private readonly IDatabase _redis;

		private string RedirectUri => $"{APP_URL}/facebook/auth/callback";

		public FaceBookController(
			ILogger<FaceBookController> logger,
			IOptions<SocialMediaSettings> options,
			IPublishEndpoint publishEndpoint,
			AppDbContext db,
			IConnectionMultiplexer redis)
		{
			_logger = logger;
			_publishEndpoint = publishEndpoint;
			_settings = options.Value;
			_db = db;
			_redis = redis.GetDatabase();
			_httpClient = new HttpClient();
		}

		private string AppId => _settings.AppId;
		private string AppSecret => _settings.AppSecret;

		// ==========================================================
		// ВЕБХУКИ FACEBOOK (MESSENGER & FEED)
		// ==========================================================
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

				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				if (root.TryGetProperty("entry", out var entryArr))
				{
					foreach (var entry in entryArr.EnumerateArray())
					{
						var pageId = entry.GetProperty("id").GetString();
						if (string.IsNullOrEmpty(pageId)) continue;

						var settings = await _db.FacebookSettings
							.AsNoTracking()
							.FirstOrDefaultAsync(s => s.PageId == pageId);

						if (settings == null || !settings.IsActive) continue;

						// 1. ПЕРЕХВАТ ЛИЧНЫХ СООБЩЕНИЙ (MESSENGER)
						if (settings.IsDirectEnabled && entry.TryGetProperty("messaging", out var messagingArr))
						{
							foreach (var mItem in messagingArr.EnumerateArray())
							{
								if (mItem.TryGetProperty("message", out var msgObj))
								{
									if (msgObj.TryGetProperty("is_echo", out var isEcho) && isEcho.GetBoolean())
										continue;

									var senderId = mItem.TryGetProperty("sender", out var s) ? s.GetProperty("id").GetString() : null;
									var mid = msgObj.TryGetProperty("mid", out var m) ? m.GetString() : null;
									var text = msgObj.TryGetProperty("text", out var t) ? t.GetString() : null;

									if (string.IsNullOrEmpty(senderId) || string.IsNullOrEmpty(mid))
										continue;

									if (senderId == pageId) continue;

									int attachCount = msgObj.TryGetProperty("attachments", out var atts) ? atts.GetArrayLength() : 0;

									_logger.LogInformation($"[Facebook Webhook] Поймано ЛС от {senderId}: «{text}» (вложений: {attachCount})");

									await _publishEndpoint.Publish(new FacebookMessageReceived
									{
										BotDbId = settings.Id,
										PageId = pageId,
										SenderId = senderId,
										MessageId = mid,
										Text = text ?? "",
										AttachmentCount = attachCount
									});
								}
							}
						}

						// 2. ПЕРЕХВАТ КОММЕНТАРИЕВ К ПУБЛИКАЦИЯМ
						if (settings.IsCommentsEnabled && entry.TryGetProperty("changes", out var changesArr))
						{
							foreach (var change in changesArr.EnumerateArray())
							{
								var field = change.TryGetProperty("field", out var f) ? f.GetString() : null;

								if (field == "feed" && change.TryGetProperty("value", out var val))
								{
									var item = val.TryGetProperty("item", out var i) ? i.GetString() : null;
									var verb = val.TryGetProperty("verb", out var v) ? v.GetString() : null;

									if (item == "comment" && verb == "add")
									{
										var commentId = val.GetProperty("comment_id").GetString()!;
										var postId = val.TryGetProperty("post_id", out var pi) ? pi.GetString() ?? "" : "";
										var message = val.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

										string senderId = "";
										string senderName = "Пользователь";

										if (val.TryGetProperty("from", out var fromObj))
										{
											senderId = fromObj.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "";
											senderName = fromObj.TryGetProperty("name", out var sname) ? sname.GetString() ?? "Пользователь" : "Пользователь";
										}

										if (senderId == pageId) continue;

										_logger.LogInformation($"[Facebook Webhook] Пойман комментарий от {senderName}: «{message}»");

										await _publishEndpoint.Publish(new FacebookCommentReceived
										{
											PageId = pageId,
											CommentId = commentId,
											PostId = postId,
											SenderId = senderId,
											SenderName = senderName,
											Text = message
										});
									}
								}
							}
						}
					}
				}

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook Webhook] Ошибка обработки вебхука");
				return StatusCode(500);
			}
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК СТРАНИЦЫ (/facebook)
		// ==========================================================
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

				// МЕТОД BaseController: считаем публикации для предупреждения перед отключением
				ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.Facebook, botId.Value);
			}
			else
			{
				ViewBag.LinkedPostsCount = 0;
			}

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			var fbScopes = "pages_manage_posts,pages_messaging,pages_show_list,pages_manage_metadata,pages_read_engagement,pages_read_user_content,business_management,public_profile,email,pages_manage_engagement,instagram_basic";
			ViewBag.FbLoginUrl = $"https://www.facebook.com/v22.0/dialog/oauth?client_id={AppId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={fbScopes}&response_type=code&auth_type=rerequest";

			return View(settings);
		}

		// ==========================================================
		// 2. ОТКЛЮЧЕНИЕ СТРАНИЦЫ (С УМНОЙ ОЧИСТКОЙ ПОСТОВ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null)
			{
				_logger.LogWarning($"[Facebook] Попытка удаления ненайденной страницы {botId} пользователем {userId}");
				return RedirectToAction("Profile", "Auth");
			}

			try
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ В ПЛАНИРОВЩИКЕ И GOOGLE DRIVE ИЗ BaseController
				await CleanupLinkedPostsAsync(NetworkType.Facebook, botId);

				// 2. Отписка от вебхуков Facebook
				if (!string.IsNullOrEmpty(settings.PageAccessToken))
				{
					try
					{
						var unsubscribeUrl = $"https://graph.facebook.com/v24.0/{settings.PageId}/subscribed_apps?access_token={settings.PageAccessToken}";
						await _httpClient.DeleteAsync(unsubscribeUrl);
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "[Facebook] Не удалось отписать страницу от вебхуков");
					}
				}

				// 3. Удаляем саму интеграцию из базы данных
				_db.FacebookSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation($"✅ [Facebook] Страница '{settings.PageName}' (BotId: {botId}) успешно отключена.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Facebook] Ошибка при отключении страницы {botId}");
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 3. АВТОРИЗАЦИЯ И СОХРАНЕНИЕ СТРАНИЦ (CALLBACK)
		// ==========================================================
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
				var shortTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
									$"client_id={AppId}&" +
									$"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
									$"client_secret={AppSecret}&" +
									$"code={code}";

				var shortResp = await _httpClient.GetFromJsonAsync<JsonElement>(shortTokenUrl);
				var shortUserToken = shortResp.GetProperty("access_token").GetString();

				var longTokenUrl = $"https://graph.facebook.com/v22.0/oauth/access_token?" +
								   $"grant_type=fb_exchange_token&" +
								   $"client_id={AppId}&" +
								   $"client_secret={AppSecret}&" +
								   $"fb_exchange_token={shortUserToken}";

				var longResp = await _httpClient.GetFromJsonAsync<JsonElement>(longTokenUrl);
				var longUserToken = longResp.GetProperty("access_token").GetString();

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

				var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
				if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
				var userId = int.Parse(userIdStr);

				List<FacebookSettings> settings = new();
				foreach (var page in pagesList)
				{
					settings.Add(await SaveFacebookPage(userId, page));
				}

				_logger.LogInformation($"[Facebook] Успешно подключено страниц: {settings.Count} для пользователя {userId}");

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

		// ==========================================================
		// 4. ОБНОВЛЕНИЕ НАСТРОЕК
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(
			int botId,
			string systemPrompt,
			int profileId,
			bool isDailyStoriesEnabled,
			string dailyStoryTime,
			bool isStoryOverlayTextEnabled,
			string? storyOverlayText,
			bool isDirectEnabled,
			int directReplyMode,
			string? directTemplates,
			bool isCommentsEnabled,
			int commentReplyMode,
			string? commentTemplates,
			string commentPrompt)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null) return RedirectToAction("Index");

			try
			{
				settings.IsActive = isDirectEnabled || isCommentsEnabled;

				settings.IsDirectEnabled = isDirectEnabled;
				settings.DirectReplyMode = directReplyMode > 0 ? directReplyMode : 2;
				settings.DirectTemplates = directTemplates;
				settings.SystemPrompt = systemPrompt ?? "";

				settings.IsCommentsEnabled = isCommentsEnabled;
				settings.CommentReplyMode = commentReplyMode > 0 ? commentReplyMode : 2;
				settings.CommentTemplates = commentTemplates;
				settings.CommentPrompt = commentPrompt ?? "";

				settings.IsDailyStoriesEnabled = isDailyStoriesEnabled;
				settings.DailyStoryTime = string.IsNullOrWhiteSpace(dailyStoryTime) ? "12:00" : dailyStoryTime.Trim();
				settings.IsStoryOverlayTextEnabled = isStoryOverlayTextEnabled;
				settings.StoryOverlayText = storyOverlayText;
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
				_logger.LogInformation($"[Facebook] Все настройки обновлены для '{settings.PageName}'.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Facebook] Ошибка сохранения настроек {botId}");
			}

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

			try
			{
				var subscribeUrl = $"https://graph.facebook.com/v24.0/{pageId}/subscribed_apps?subscribed_fields=messages,messaging_postbacks,feed&access_token={pageToken}";
				await _httpClient.PostAsync(subscribeUrl, null);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, $"[Facebook] Не удалось подписать страницу {pageName} на вебхуки Messenger");
			}

			var settings = await _db.FacebookSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.PageId == pageId);

			if (settings == null)
			{
				// БЕЗОПАСНЫЙ ПРОФИЛЬ ИЗ BaseController (БЕЗ ОШИБКИ NULLABLE):
				int profileId = await GetActiveProfileIdSafeAsync(_db, userId);

				settings = new FacebookSettings
				{
					UserId = userId,
					PageId = pageId,
					ProfileId = profileId
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
			settings.LinkedInstagramBusinessId = await GetLinkedInstagramBusinessAccountIdAsync(pageId, pageToken);

			await _db.SaveChangesAsync();
			_logger.LogInformation($"[Facebook] Страница '{pageName}' ({pageId}) сохранена в БД для пользователя {userId}");

			return settings;
		}
		

		private async Task<string?> GetLinkedInstagramBusinessAccountIdAsync(string pageId, string pageToken)
		{
			try
			{
				string url = $"https://graph.facebook.com/v24.0/{pageId}?fields=instagram_business_account&access_token={pageToken}";
				var response = await _httpClient.GetAsync(url);
				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(json);
					if (doc.RootElement.TryGetProperty("instagram_business_account", out var igAccount) &&
						igAccount.TryGetProperty("id", out var idElem))
					{
						return idElem.GetString();
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "[Facebook] Не удалось проверить привязку Instagram для страницы {PageId}", pageId);
			}
			return null;
		}
	}
}