using System.Security.Claims;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Models;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Route("threads")]
	public class ThreadsController : Controller
	{
		private readonly ILogger<ThreadsController> _logger;
		private readonly AppDbContext _db;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly HttpClient _httpClient;
		private readonly SocialMediaSettings _settings;
		private const string VerifyToken = "test"; // Задайте свой токен

		// Специфичные настройки для Threads (нужно добавить в твой SocialMediaSettings класс)
		private string ThreadsAppId => _settings.ThreadsAppId;
		private string ThreadsAppSecret => _settings.ThreadsAppSecret;
		private string RedirectUri => $"{APP_URL}/threads/auth/callback";

		public ThreadsController(ILogger<ThreadsController> logger, AppDbContext db
			, IOptions<SocialMediaSettings> options, IPublishEndpoint publishEndpoint)
		{
			_logger = logger;
			_db = db;
			_publishEndpoint = publishEndpoint;
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

				// Логируем для отладки
				_logger.LogInformation($"[Threads Webhook Raw]: {body}");

				using var doc = JsonDocument.Parse(body);
				var root = doc.RootElement;

				// Проверяем поле "topic" (в Threads оно в корне)
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
								// 1. Получаем имя автора сообщения
								var authorUsername = val.GetProperty("username").GetString();

								// 2. Получаем имя бота (владельца)
								string? botUsername = null;
								string? botThreadsId = null;

								if (val.TryGetProperty("root_post", out var rootPost))
								{
									botUsername = rootPost.GetProperty("username").GetString();
									botThreadsId = rootPost.GetProperty("owner_id").GetString();
								}

								// --- ЗАЩИТА ОТ САМОГО СЕБЯ ---
								// Если автор сообщения и есть наш бот - игнорируем
								if (!string.IsNullOrEmpty(authorUsername) &&
									authorUsername.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
								{
									_logger.LogInformation($"[Threads] Игнорируем эхо-сообщение от самого бота (@{authorUsername})");
									continue;
								}

								if (string.IsNullOrEmpty(botThreadsId)) continue;

								var text = val.GetProperty("text").GetString();
								var mediaId = val.GetProperty("id").GetString();

								_logger.LogInformation($"[Threads] Пойман {field} от {authorUsername}: {text}");

								await _publishEndpoint.Publish(new ThreadsEventReceived
								{
									BotThreadsId = botThreadsId,
									Type = field,
									MediaId = mediaId!,
									Text = text ?? "",
									Username = authorUsername ?? "user"
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
				return Ok(); // Всегда возвращаем 200, чтобы Meta не зациклила ретраи
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
				var settings = await AddUserToDb(longToken,
									   meRoot.GetProperty("id").GetString(),
									   meRoot.GetProperty("username").GetString(),
									   meRoot.TryGetProperty("threads_profile_picture_url", out var p) ? p.GetString() : null,
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
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// 1. Ищем, нет ли у этого пользователя уже настроек для этого КОНКРЕТНОГО Threads-аккаунта
			// Ищем по ThreadsUserId, а не просто по UserId
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.ThreadsUserId == threadsId);

			bool isNew = false;
			if (settings == null)
			{
				// 2. Если такого аккаунта еще нет у юзера — создаем новый объект
				settings = new ThreadsSettings
				{
					UserId = userId,
					ThreadsUserId = threadsId
				};
				_db.ThreadsSettings.Add(settings);
				isNew = true;
			}

			// 3. Обновляем данные (и для новых, и для существующих)
			settings.AccessToken = token;
			settings.Username = username;
			settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
			settings.IsActive = true;

			// Рекомендую здесь тоже использовать скачивание в Base64, как мы делали для Инсты
			// Чтобы аватарка не пропадала через неделю
			if (!string.IsNullOrEmpty(picUrl))
			{
				settings.ProfilePictureUrl = await DownloadImageAsBase64(picUrl);
			}

			// 4. Сохраняем изменения
			await _db.SaveChangesAsync();

			_logger.LogInformation(isNew
				? $"[Threads] Добавлен новый аккаунт @{username}"
				: $"[Threads] Обновлен токен для существующего аккаунта @{username}");

			return settings;
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null && !string.IsNullOrEmpty(settings.AccessToken))
			{
				// Сначала пытаемся честно отписаться от вебхуков
				try
				{
					//await ManageWebhooksAsync(settings.AccessToken, false);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Could not unsubscribe before disconnect. proceeding anyway.");
				}

				await DisconnectThreadUser(settings.ThreadsUserId, fullDataDelete: true);
			}

			return RedirectToAction("Profile", "Auth");
		}

		[AllowAnonymous]
		[HttpGet("deauth")]
		[HttpPost("deauth")]
		public async Task<IActionResult> DeauthorizationCallback([FromForm] string signed_request = null)
		{
			_logger.LogInformation($"=== Deauthorization callback received ===");
			_logger.LogInformation(signed_request);

			try
			{
				if (string.IsNullOrEmpty(signed_request)) return Ok();

				var threadUserId = ParseSignedRequest(signed_request);
				if (!string.IsNullOrEmpty(threadUserId))
				{
					_logger.LogInformation($"User {threadUserId} deauthorized app. Cleaning up token...");

					// Вызываем наш метод очистки (false = не удалять всё, только токен)
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
		public async Task<IActionResult> DataDeletionCallback(
			[FromForm] string signed_request = null)
		{
			_logger.LogInformation($"=== Data Deletion callback received ===");
			_logger.LogInformation(signed_request);

			try
			{
				string userId = null;
				string confirmationCode = Guid.NewGuid().ToString("N");

				if (!string.IsNullOrEmpty(signed_request))
				{
					userId = ParseSignedRequest(signed_request);
				}

				if (!string.IsNullOrEmpty(userId))
				{
					_logger.LogInformation($"Processing FULL DATA DELETION for user: {userId}");

					// Удаляем данные полностью (true)
					await DisconnectThreadUser(userId, fullDataDelete: true);
				}

				// Генерируем URL статуса (его нужно реализовать ниже)
				var statusUrl = $"{APP_URL}/instagram/deletion-status/{confirmationCode}";

				var response = new
				{
					url = statusUrl,
					confirmation_code = confirmationCode,
					status = "success" // Мы удалили данные синхронно, так что сразу success
				};

				return Ok(response);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing data deletion");
				return Ok(new { url = $"{APP_URL}", confirmation_code = "error", status = "error" });
			}
		}

		private string ParseSignedRequest(string signedRequest)
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
				var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);

				dynamic data = JsonConvert.DeserializeObject<dynamic>(payloadJson);
				return data.user_id?.ToString();
			}
			catch
			{
				return null;
			}
		}

		private async Task<bool> DisconnectThreadUser(string threadUserId, bool fullDataDelete)
		{
			// Ищем настройки, где BusinessId совпадает с ID из вебхука
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.ThreadsUserId == threadUserId);

			if (settings == null)
			{
				_logger.LogInformation($"[Threads] Попытка удаления для {threadUserId}, но данных в базе уже нет. Всё ок.");
				return true;
			}

			try
			{
				// Логика отписки (опционально)
				if (!string.IsNullOrEmpty(settings.AccessToken))
				{
					_logger.LogInformation($"[Threads] Пытаемся отписать вебхуки для {threadUserId}...");
					// Здесь будет твой вызов ManageWebhooksAsync, если он реализован
				}

				if (fullDataDelete)
				{
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

				// 2. Пытаемся сохранить изменения
				await _db.SaveChangesAsync();
				return true;
			}
			catch (DbUpdateConcurrencyException)
			{
				// 3. Если мы попали сюда, значит кто-то другой (параллельный запрос) 
				// уже удалил или изменил эту запись. 
				// В нашем случае это успех — данных больше нет.
				_logger.LogWarning($"[Threads] Конфликт параллельного доступа при удалении {threadUserId}. Игнорируем, так как запись уже обработана.");
				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[Threads] Ошибка при отключении пользователя {threadUserId}");
				return false;
			}
		}

		[HttpPost("update-settings")]
		[Authorize]
		public async Task<IActionResult> UpdateSettings(int botId, string systemPrompt)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// Ищем настройки бота по его Id И проверяем, что он принадлежит текущему юзеру
			var settings = await _db.ThreadsSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				return RedirectToAction("Index");

			try
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				bool isActive = isActiveRaw.Contains("true");

				// 2. Управление вебхуками (если статус изменился)
				if (settings.IsActive != isActive)
				{
					_logger.LogInformation($"Изменение статуса вебхуков для бота {botId} (User {userId}): {settings.IsActive} -> {isActive}");

					//bool success = await ManageWebhooksAsync(settings.AccessToken, newIsActiveStatus);
					//if (!success)
					//{
					//	_logger.LogWarning($"[Meta API] Не удалось обновить подписку на вебхуки для бота {botId}");
					//}
				}

				// 3. Обновляем модель
				settings.IsActive = isActive;
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
