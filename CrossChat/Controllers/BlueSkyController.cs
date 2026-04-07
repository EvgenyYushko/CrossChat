using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("bluesky")]
	public class BlueSkyController : Controller
	{
		private readonly ILogger<BlueSkyController> _logger;
		private readonly AppDbContext _db;
		private readonly HttpClient _httpClient;
		private string ClientId => $"{APP_URL}/bluesky/client-metadata.json";
		private string RedirectUri => $"{APP_URL}/bluesky/auth/callback";

		public BlueSkyController(ILogger<BlueSkyController> logger, AppDbContext db)
		{
			_logger = logger;
			_db = db;
			_httpClient = new HttpClient();
		}

		[HttpGet]
		public async Task<IActionResult> Index()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);
			return View(settings);
		}

		[HttpPost("connect")]
		public async Task<IActionResult> Connect(string handle)
		{
			handle = handle.Replace("@", "").Trim().ToLower();

			try
			{
				// А. Разрешаем handle в DID (узнаем реальный ID пользователя)
				var resolveUrl = $"https://bsky.social/xrpc/com.atproto.identity.resolveHandle?handle={handle}";
				var resolveResp = await _httpClient.GetAsync(resolveUrl);
				if (!resolveResp.IsSuccessStatusCode) return BadRequest("Неверный Handle");

				var resolveJson = await resolveResp.Content.ReadFromJsonAsync<JsonElement>();
				string did = resolveJson.GetProperty("did").GetString();

				// Б. Генерируем PKCE (Code Verifier и Challenge)
				var codeVerifier = GenerateRandomString(64);
				var codeChallenge = GenerateCodeChallenge(codeVerifier);
				var state = Guid.NewGuid().ToString("N");

				// Сохраняем verifier и state в сессию (они понадобятся в Callback)
				HttpContext.Session.SetString("bsky_verifier", codeVerifier);
				HttpContext.Session.SetString("bsky_state", state);
				HttpContext.Session.SetString("bsky_handle", handle);
				HttpContext.Session.SetString("bsky_did", did);

				// В. Формируем URL для редиректа на PDS пользователя
				// Для простоты используем bsky.social, но по протоколу нужно искать сервер через DID
				var authEndpoint = "https://bsky.social/oauth/authorize";

				var url = $"{authEndpoint}?" +
						  $"client_id={Uri.EscapeDataString(ClientId)}&" +
						  $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
						  $"response_type=code&" +
						  $"scope=atproto%20transition:generic&" +
						  $"state={state}&" +
						  $"code_challenge={codeChallenge}&" +
						  $"code_challenge_method=S256&" +
						  $"login_hint={handle}";

				return Redirect(url);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "BlueSky Connect Error");
				return RedirectToAction("Index");
			}
		}

		// ==========================================================
		// 2. ОБРАБОТКА ОТВЕТА (CALLBACK)
		// ==========================================================
		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback(string code, string state)
		{
			// 1. Логируем входящие данные
			_logger.LogInformation($"[BlueSky] Callback received. Code length: {code?.Length ?? 0}, State: {state}");

			var savedState = HttpContext.Session.GetString("bsky_state");
			var codeVerifier = HttpContext.Session.GetString("bsky_verifier");

			// Проверка безопасности
			if (string.IsNullOrEmpty(code) || state != savedState || string.IsNullOrEmpty(codeVerifier))
			{
				_logger.LogError($"[BlueSky] Security check failed. Code present: {!string.IsNullOrEmpty(code)}, State match: {state == savedState}");
				return BadRequest("Ошибка авторизации: неверный state или отсутствует код.");
			}

			try
			{
				// 2. Формируем запрос на обмен токена
				var tokenUrl = "https://bsky.social/oauth/token";

				// ВАЖНО: Используем Dictionary для данных формы
				var values = new Dictionary<string, string>
				{
					{ "grant_type", "authorization_code" },
					{ "code", code },
					{ "client_id", ClientId }, // Должен быть https://crosschat.ru/bluesky/client-metadata.json
					{ "redirect_uri", RedirectUri },
					{ "code_verifier", codeVerifier }
				};

				var requestContent = new FormUrlEncodedContent(values);

				// 3. Отправляем запрос
				_logger.LogInformation($"[BlueSky] Exchanging code for token at {tokenUrl}...");
				var response = await _httpClient.PostAsync(tokenUrl, requestContent);
				var responseBody = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError($"[BlueSky] Token exchange FAILED: {responseBody}");
					return Content($"Ошибка обмена токена: {responseBody}");
				}

				// 4. Парсим ответ
				using var doc = JsonDocument.Parse(responseBody);
				var root = doc.RootElement;

				string accessToken = root.GetProperty("access_token").GetString()!;
				string refreshToken = root.GetProperty("refresh_token").GetString()!;

				// Достаем сохраненные данные из сессии
				string handle = HttpContext.Session.GetString("bsky_handle") ?? "unknown";
				string did = HttpContext.Session.GetString("bsky_did") ?? "unknown";

				// 5. Сохраняем в БД
				await SaveToken(accessToken, refreshToken, handle, did);

				_logger.LogInformation($"[BlueSky] ✅ Аккаунт {handle} успешно подключен!");

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Critical error in Callback");
				return RedirectToAction("Index");
			}
		}

		// ==========================================================
		// ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ PKCE
		// ==========================================================
		private string GenerateRandomString(int length)
		{
			const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
			return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
		}

		private string GenerateCodeChallenge(string verifier)
		{
			using var sha256 = SHA256.Create();
			var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(verifier));
			return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
		}

		private async Task SaveToken(string access, string refresh, string handle, string did)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId)
						   ?? new BlueSkySettings { UserId = userId };

			settings.AccessToken = access;
			settings.RefreshToken = refresh;
			settings.Handle = handle;
			settings.Did = did;
			settings.IsActive = true;

			if (settings.UserId == 0 || !await _db.BlueSkySettings.AnyAsync(s => s.UserId == userId))
				_db.BlueSkySettings.Add(settings);
			else
				_db.BlueSkySettings.Update(settings);

			await _db.SaveChangesAsync();
		}

		[AllowAnonymous]
		[HttpGet("client-metadata.json")]
		public IActionResult GetMetadata()
		{
			return Ok(new
			{
				client_id = $"{APP_URL}/bluesky/client-metadata.json",
				client_name = "CrossChat AI Bot",
				client_uri = APP_URL,
				redirect_uris = new[] { $"{APP_URL}/bluesky/auth/callback" },
				scope = "atproto transition:generic",
				grant_types = new[] { "authorization_code", "refresh_token" },
				response_types = new[] { "code" },
				token_endpoint_auth_method = "none", // Для работы без Client Secret
				application_type = "web"
			});
		}
	}
}
