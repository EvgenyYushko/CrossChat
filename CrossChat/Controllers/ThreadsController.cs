using System.Security.Claims;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Route("threads")]
	public class ThreadsController : Controller
	{
		private readonly ILogger<ThreadsController> _logger;
		private readonly AppDbContext _db;
		private readonly HttpClient _httpClient;
		private readonly SocialMediaSettings _settings;
		private const string VerifyToken = "test"; // Задайте свой токен

		// Специфичные настройки для Threads (нужно добавить в твой SocialMediaSettings класс)
		private string ThreadsAppId => _settings.ThreadsAppId;
		private string ThreadsAppSecret => _settings.ThreadsAppSecret;
		private string RedirectUri => $"{APP_URL}/threads/auth/callback";

		public ThreadsController(ILogger<ThreadsController> logger, AppDbContext db, IOptions<SocialMediaSettings> options)
		{
			_logger = logger;
			_db = db;
			_settings = options.Value;
			_httpClient = new HttpClient();
		}

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

				// 2. Возвращаем именно Content, чтобы это была чистая строка без HTML-оберток
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
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			// Формируем ссылку на авторизацию Threads
			var scopes = string.Join(",", 
				"threads_basic",            // Профиль
				"threads_content_publish",  // Постить новые треды
				"threads_manage_replies",   // Отвечать на реплаи
				"threads_read_replies",     // ЧИТАТЬ реплаи пользователей (ВАЖНО!)
				"threads_manage_mentions",  // Видеть упоминания (ВАЖНО!)
				"threads_manage_insights"   // Статистика
			);

			ViewBag.LoginUrl = $"https://www.threads.net/oauth/authorize?" +
							   $"client_id={ThreadsAppId}&" +
							   $"redirect_uri={RedirectUri}&" +
							   $"scope={scopes}&" +
							   $"response_type=code";

			return View(settings);
		}

		[HttpGet("auth/callback")]
		public async Task<IActionResult> Callback(string? code, string? error)
		{
			if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
				return RedirectToAction("Index");

			_logger.LogInformation(code);
			_logger.LogInformation(error);

			try
			{
				// 1. Обмен кода на Short-Lived Token (через graph.threads.net)
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

				_logger.LogInformation(shortToken);


				// 2. Обмен на Long-Lived Token (60 дней)
				var longUrl = $"https://graph.threads.net/access_token?grant_type=th_exchange_token&client_secret={ThreadsAppSecret}&access_token={shortToken}";
				var longResp = await _httpClient.GetAsync(longUrl);
				var longJson = await longResp.Content.ReadAsStringAsync();
				using var longDoc = JsonDocument.Parse(longJson);

				_logger.LogInformation(longJson);

				var longToken = longDoc.RootElement.GetProperty("access_token").GetString();
				var expiresIn = longDoc.RootElement.GetProperty("expires_in").GetInt32();

				// 3. Получение данных профиля
				var meUrl = $"https://graph.threads.net/me?fields=id,username,threads_profile_picture_url&access_token={longToken}";
				var meResp = await _httpClient.GetAsync(meUrl);
				using var meDoc = JsonDocument.Parse(await meResp.Content.ReadAsStringAsync());
				var meRoot = meDoc.RootElement;

				_logger.LogInformation(meRoot.GetProperty("id").GetString());
				_logger.LogInformation(meRoot.GetProperty("username").GetString());

				// 4. Сохранение в БД
				await SaveThreadsToken(longToken,
									   meRoot.GetProperty("id").GetString(),
									   meRoot.GetProperty("username").GetString(),
									   meRoot.TryGetProperty("threads_profile_picture_url", out var p) ? p.GetString() : null,
									   expiresIn);

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Threads Auth Error");
				return RedirectToAction("Index");
			}
		}

		private async Task SaveThreadsToken(string token, string threadsId, string username, string? picUrl, int expiresIn)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.UserId == userId)
						   ?? new ThreadsSettings { UserId = userId };

			settings.AccessToken = token;
			settings.ThreadsUserId = threadsId;
			settings.Username = username;
			settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
			settings.ProfilePictureUrl = picUrl; // Можно также скачать в Base64 как для Инсты
			settings.IsActive = true;

			if (await _db.ThreadsSettings.AnyAsync(s => s.UserId == userId)) _db.ThreadsSettings.Update(settings);
			else _db.ThreadsSettings.Add(settings);

			await _db.SaveChangesAsync();
		}

		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt, bool isActive)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Ищем настройки бота по его Id И проверяем, что он принадлежит текущему юзеру
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				// 1. Вычисляем новый общий статус активности
				bool newIsActiveStatus = isActive;

				// 2. Управление вебхуками (если статус изменился)
				if (settings.IsActive != newIsActiveStatus)
				{
					_logger.LogInformation($"Изменение статуса вебхуков для бота {botId} (User {userId}): {settings.IsActive} -> {newIsActiveStatus}");

					//bool success = await ManageWebhooksAsync(settings.AccessToken, newIsActiveStatus);
					//if (!success)
					//{
					//	_logger.LogWarning($"[Meta API] Не удалось обновить подписку на вебхуки для бота {botId}");
					//}
				}

				// 3. Обновляем модель
				settings.IsActive = newIsActiveStatus;
				settings.SystemPrompt = systemPrompt ?? "";

				await _db.SaveChangesAsync();
				_logger.LogInformation($"Настройки бота {botId} успешно сохранены.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"Ошибка при обновлении настроек бота {botId}");
			}

			return RedirectToAction("Index", new { botId = botId });
		}
	}
}
