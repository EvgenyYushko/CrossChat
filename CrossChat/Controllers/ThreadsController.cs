using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Models;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("threads")]
	public class ThreadsController : BaseController
	{
		private readonly ILogger<ThreadsController> _logger;
		private readonly AppDbContext _db;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IThreadsService _threadsService;
		private readonly HttpClient _httpClient;
		private readonly SocialMediaSettings _settings;
		private const string VerifyToken = "test";

		private string ThreadsAppId => _settings.ThreadsAppId;
		private string ThreadsAppSecret => _settings.ThreadsAppSecret;
		private string RedirectUri => $"{APP_URL}/threads/auth/callback";

		public ThreadsController(
			ILogger<ThreadsController> logger, 
			AppDbContext db,
			IOptions<SocialMediaSettings> options, 
			IPublishEndpoint publishEndpoint, 
			IThreadsService threadsService)
		{
			_logger = logger;
			_db = db;
			_publishEndpoint = publishEndpoint;
			_threadsService = threadsService;
			_settings = options.Value;
			_httpClient = new HttpClient();
		}

		// ==========================================================
		// ВЕБХУКИ THREADS (REPLIES & MENTIONS)
		// ==========================================================
		[AllowAnonymous]
		[HttpGet("webhook")]
		public IActionResult VerifyWebhook(
			[FromQuery(Name = "hub.mode")] string mode,
			[FromQuery(Name = "hub.verify_token")] string token,
			[FromQuery(Name = "hub.challenge")] string challenge)
		{
			_logger.LogInformation($"Threads Webhook verification: mode={mode}, token={token}");

			if (mode == "subscribe" && token == VerifyToken)
			{
				_logger.LogInformation("Webhook verified successfully");
				return Ok(challenge);
			}

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

				_logger.LogInformation($"[Threads Webhook Raw]: {body}");

				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				if (root.TryGetProperty("topic", out var topic) &&
				   (topic.GetString() == "moderate" || topic.GetString() == "interaction"))
				{
					if (root.TryGetProperty("values", out var values))
					{
						foreach (var item in values.EnumerateArray())
						{
							var field = item.GetProperty("field").GetString();
							var val = item.GetProperty("value");

							if (field == "replies" || field == "mentions")
							{
								var authorUsername = val.GetProperty("username").GetString();

								string? botUsername = null;
								string? botThreadsId = null;
								string? rootPostId = null;

								if (val.TryGetProperty("root_post", out var rootPost))
								{
									botUsername = rootPost.TryGetProperty("username", out var bu) ? bu.GetString() : null;
									botThreadsId = rootPost.TryGetProperty("owner_id", out var bo) ? bo.GetString() : null;
									rootPostId = rootPost.TryGetProperty("id", out var bi) ? bi.GetString() : null;
								}

								// 1. ЗАЩИТА ОТ САМОГО СЕБЯ (Эхо)
								if (!string.IsNullOrEmpty(authorUsername) &&
									authorUsername.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
								{
									_logger.LogInformation($"[Threads] Игнорируем эхо-сообщение от самого бота (@{authorUsername})");
									continue;
								}

								if (string.IsNullOrEmpty(botThreadsId)) continue;

								// 2. ГЛАВНАЯ ЗАЩИТА: РАЗРЫВ БЕСКОНЕЧНОЙ ЦЕПОЧКИ
								if (field == "replies" && val.TryGetProperty("replied_to", out var repliedTo))
								{
									var repliedToId = repliedTo.TryGetProperty("id", out var rId) ? rId.GetString() : null;
									var repliedToUsername = repliedTo.TryGetProperty("username", out var rUser) ? rUser.GetString() : null;

									if (!string.IsNullOrEmpty(repliedToUsername) &&
										repliedToUsername.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
									{
										_logger.LogInformation($"[Threads] Пользователь @{authorUsername} ответил на комментарий бота. Игнорируем.");
										continue;
									}

									if (!string.IsNullOrEmpty(repliedToId) && !string.IsNullOrEmpty(rootPostId) && repliedToId != rootPostId)
									{
										_logger.LogInformation($"[Threads] Игнорируем вложенный реплай от @{authorUsername}.");
										continue;
									}
								}

								var text = val.GetProperty("text").GetString();
								var mediaId = val.GetProperty("id").GetString();

								_logger.LogInformation($"[Threads] Пойман {field} от {authorUsername}: {text}");

								await _publishEndpoint.Publish(new ThreadsEventReceived
								{
									BotThreadsId = botThreadsId,
									Type = field,
									MediaId = mediaId!,
									Text = text ?? "",
									Username = authorUsername ?? "user",
									RootPostId = rootPostId
								});
							}
						}
					}
				}

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing Threads webhook");
				return Ok();
			}
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК (/threads)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.ThreadsSettings
				.Include(p => p.Profile)
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			// МЕТОД ИЗ BaseController: считаем посты для предупреждения перед отключением
			ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.Threads, botId);

			var scopes = string.Join(",",
				"threads_basic",
				"threads_content_publish",
				"threads_manage_replies",
				"threads_read_replies",
				"threads_manage_mentions",
				"threads_manage_insights"
			);

			ViewBag.LoginUrl = $"https://www.threads.net/oauth/authorize?" +
							   $"client_id={ThreadsAppId}&" +
							   $"redirect_uri={RedirectUri}&" +
							   $"scope={scopes}&" +
							   $"response_type=code";

			return View(settings);
		}

		// ==========================================================
		// 2. ОТКЛЮЧЕНИЕ АККАУНТА (С УМНОЙ ОЧИСТКОЙ ПОСТОВ)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ В ПЛАНИРОВЩИКЕ И ФАЙЛОВ GOOGLE DRIVE:
				await CleanupLinkedPostsAsync(NetworkType.Threads, botId);

				// 2. Отписка и удаление самого аккаунта
				await DisconnectThreadUser(settings.ThreadsUserId, fullDataDelete: true);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 3. АВТОРИЗАЦИЯ И CALLBACK
		// ==========================================================
		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string? code, string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return RedirectToAction("Index");

			try
			{
				var formData = new Dictionary<string, string>
				{
					{ "client_id", ThreadsAppId },
					{ "client_secret", ThreadsAppSecret },
					{ "grant_type", "authorization_code" },
					{ "redirect_uri", RedirectUri },
					{ "code", code }
				};

				var shortResp = await _httpClient.PostAsync("https://graph.threads.net/oauth/access_token", new FormUrlEncodedContent(formData));
				var shortJson = await shortResp.Content.ReadAsStringAsync();
				using var shortDoc = JsonDocument.Parse(shortJson);
				var shortToken = shortDoc.RootElement.GetProperty("access_token").GetString();

				var longUrl = $"https://graph.threads.net/access_token?grant_type=th_exchange_token&client_secret={ThreadsAppSecret}&access_token={shortToken}";
				var longResp = await _httpClient.GetAsync(longUrl);
				var longJson = await longResp.Content.ReadAsStringAsync();
				using var longDoc = JsonDocument.Parse(longJson);

				var longToken = longDoc.RootElement.GetProperty("access_token").GetString()!;
				var expiresIn = longDoc.RootElement.GetProperty("expires_in").GetInt32();

				var profile = await _threadsService.GetThreadsUserProfileAsync(longToken);
				if (profile == null)
				{
					return RedirectToAction("Index", new { error = "failed_to_get_profile" });
				}

				var settings = await AddUserToDb(
					longToken,
					profile.Id,
					profile.Username,
					profile.ProfilePictureUrl,
					expiresIn);

				return RedirectToAction("Index", new { botId = settings?.Id ?? 0 });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Threads Auth Error");
				return RedirectToAction("Index");
			}
		}

		private async Task<ThreadsSettings> AddUserToDb(string token, string threadsId, string username, string? picUrl, int expiresIn)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.ThreadsUserId == threadsId);

			bool isNew = false;
			if (settings == null)
			{
				// БЕЗОПАСНЫЙ ПРОФИЛЬ ИЗ BaseController (БЕЗ ОШИБКИ NULLABLE):
				int profileId = await GetActiveProfileIdSafeAsync(_db, userId);

				settings = new ThreadsSettings
				{
					UserId = userId,
					ThreadsUserId = threadsId,
					ProfileId = profileId
				};
				_db.ThreadsSettings.Add(settings);
				isNew = true;
			}

			settings.AccessToken = token;
			settings.Username = username;
			// ВАЖНО: Срок жизни токена строго в UTC!
			settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
			settings.IsActive = true;

			if (!string.IsNullOrEmpty(picUrl))
			{
				settings.ProfilePictureUrl = await DownloadImageAsBase64ForHtml(picUrl);
			}

			await _db.SaveChangesAsync();

			_logger.LogInformation(isNew
				? $"[Threads] Добавлен новый аккаунт @{username}"
				: $"[Threads] Обновлен токен для существующего аккаунта @{username}");

			return settings;
		}

		private async Task<bool> DisconnectThreadUser(string threadUserId, bool fullDataDelete)
		{
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.ThreadsUserId == threadUserId);

			if (settings == null)
			{
				_logger.LogInformation($"[Threads] Попытка удаления для {threadUserId}, но данных в базе уже нет.");
				return true;
			}

			try
			{
				if (fullDataDelete)
				{
					// Если удаление вызвано пользователем или по GDPR вебхуку — чистим посты
					await CleanupLinkedPostsAsync(NetworkType.Threads, settings.Id);

					_db.ThreadsSettings.Remove(settings);
					_logger.LogInformation($"[Threads] Удаление записи полностью для: {threadUserId}");
				}
				else
				{
					settings.AccessToken = null;
					settings.IsActive = false;
					settings.TokenExpiresAt = null;
					_logger.LogInformation($"[Threads] Очистка токена (Deauth) для: {threadUserId}");
				}

				await _db.SaveChangesAsync();
				return true;
			}
			catch (DbUpdateConcurrencyException)
			{
				_logger.LogWarning($"[Threads] Конфликт параллельного доступа при удалении {threadUserId}.");
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Threads] Ошибка при отключении пользователя {threadUserId}");
				return false;
			}
		}

		// ==========================================================
		// 4. ОБНОВЛЕНИЕ НАСТРОЕК (ПРОМПТ / ШАБЛОНЫ / РЕЖИМЫ)
		// ==========================================================
		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(
			int botId,
			string systemPrompt,
			int profileId,
			int replyMode,
			string? replyTemplates)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				settings.IsActive = isActive;
				settings.SystemPrompt = systemPrompt ?? "";
				settings.ProfileId = profileId;

				settings.ReplyMode = replyMode > 0 ? replyMode : 2;
				settings.ReplyTemplates = replyTemplates;

				await _db.SaveChangesAsync();
				_logger.LogInformation($"Настройки бота Threads '{settings.Username}' обновлены.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Ошибка при обновлении настроек Threads {botId}");
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		[AllowAnonymous]
		[HttpGet("deauth")]
		[HttpPost("deauth")]
		public async Task<IActionResult> DeauthorizationCallback([FromForm] string signed_request = null!)
		{
			try
			{
				if (string.IsNullOrEmpty(signed_request)) return Ok();

				var threadUserId = ParseSignedRequest(signed_request);
				if (!string.IsNullOrEmpty(threadUserId))
				{
					await DisconnectThreadUser(threadUserId, fullDataDelete: true);
				}

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing deauthorization");
				return Ok();
			}
		}

		[AllowAnonymous]
		[HttpGet("data-deletion")]
		[HttpPost("data-deletion")]
		public async Task<IActionResult> DataDeletionCallback([FromForm] string signed_request = null!)
		{
			try
			{
				string? userId = null;
				string confirmationCode = Guid.NewGuid().ToString("N");

				if (!string.IsNullOrEmpty(signed_request))
				{
					userId = ParseSignedRequest(signed_request);
				}

				if (!string.IsNullOrEmpty(userId))
				{
					await DisconnectThreadUser(userId, fullDataDelete: true);
				}

				var statusUrl = $"{APP_URL}/instagram/deletion-status/{confirmationCode}";
				return Ok(new { url = statusUrl, confirmation_code = confirmationCode, status = "success" });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing data deletion");
				return Ok(new { url = $"{APP_URL}", confirmation_code = "error", status = "error" });
			}
		}

		private string? ParseSignedRequest(string signedRequest)
		{
			try
			{
				var parts = signedRequest.Split('.');
				if (parts.Length != 2) return null;

				var payload = parts[1].Replace('-', '+').Replace('_', '/');
				switch (payload.Length % 4)
				{
					case 2: payload += "=="; break;
					case 3: payload += "="; break;
				}

				var payloadBytes = Convert.FromBase64String(payload);
				var payloadJson = Encoding.UTF8.GetString(payloadBytes);

				dynamic? data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
				return data?.user_id?.ToString();
			}
			catch
			{
				return null;
			}
		}
	}
}