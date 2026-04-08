using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
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
		private readonly IDistributedCache _cache;
		private readonly IBlueSkyService _blueSkyService;

		public BlueSkyController(ILogger<BlueSkyController> logger, AppDbContext db, IDistributedCache cache, IBlueSkyService blueSkyService)

		{
			_logger = logger;
			_db = db;
			_httpClient = new HttpClient();
			_cache = cache; // Используем кеш вместо сессии
			_blueSkyService = blueSkyService;
		}

		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);
			return View(settings);
		}

		[HttpGet("test-api")]
		public async Task<IActionResult> TestApi()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken) || string.IsNullOrEmpty(settings.PdsUrl))
				return Content("Данные или PDS URL не найдены. Переподключите аккаунт.");

			// 1. Формируем URL и данные поста
			var apiUrl = $"{settings.PdsUrl.TrimEnd('/')}/xrpc/com.atproto.repo.createRecord";
			var payload = new
			{
				repo = settings.Did,
				collection = "app.bsky.feed.post",
				record = new
				{
					text = "Проверка связи! Бот CrossChat теперь умеет работать с DPoP Nonce 🛡️ #atproto",
					createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
				}
			};

			try
			{
				// --- ПОПЫТКА №1 (без nonce) ---
				var (dpopProof, _) = _blueSkyService.CreateDPoPProof("POST", apiUrl, settings.PrivateKeyJson, null, settings.AccessToken);

				var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
				{
					Content = JsonContent.Create(payload)
				};
				request.Headers.Add("Authorization", $"DPoP {settings.AccessToken}");
				request.Headers.Add("DPoP", dpopProof);

				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				// --- ПРОВЕРКА НА ТРЕБОВАНИЕ NONCE ---
				if (!response.IsSuccessStatusCode && json.Contains("use_dpop_nonce"))
				{
					_logger.LogInformation("[BlueSky] PDS запросил Nonce. Повторяем запрос...");

					if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
					{
						var serverNonce = nonceValues.First();

						// --- ПОПЫТКА №2 (с полученным nonce) ---
						// Используем тот же ключ из настроек и метод POST
						var (retryDpopProof, _) = _blueSkyService.CreateDPoPProof("POST", apiUrl, settings.PrivateKeyJson, serverNonce, settings.AccessToken);

						// Создаем новый запрос (старый объект request использовать нельзя)
						var retryRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
						{
							Content = JsonContent.Create(payload)
						};
						retryRequest.Headers.Add("Authorization", $"DPoP {settings.AccessToken}");
						retryRequest.Headers.Add("DPoP", retryDpopProof);

						response = await _httpClient.SendAsync(retryRequest);
						json = await response.Content.ReadAsStringAsync();
					}
				}

				if (response.IsSuccessStatusCode)
				{
					return Content($"УСПЕХ! Пост создан. Ответ: {json}");
				}

				return Content($"Ошибка после повтора: {response.StatusCode} - {json}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Критическая ошибка в TestApi");
				return Content($"Критическая ошибка: {ex.Message}");
			}
		}

		[HttpPost("connect")]
		public async Task<IActionResult> Connect(string handle)
		{
			var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (string.IsNullOrEmpty(userId)) return Unauthorized();

			handle = handle.Replace("@", "").Trim().ToLower();
			try
			{
				var resolveUrl = $"https://bsky.social/xrpc/com.atproto.identity.resolveHandle?handle={handle}";
				var resolveResp = await _httpClient.GetAsync(resolveUrl);
				var resolveJson = await resolveResp.Content.ReadFromJsonAsync<JsonElement>();
				string did = resolveJson.GetProperty("did").GetString()!;

				// 2. Узнаем PDS (Где реально лежат данные)
				// Запрашиваем документ DID
				var didDocResp = await _httpClient.GetAsync($"https://plc.directory/{did}");
				var didDoc = await didDocResp.Content.ReadFromJsonAsync<JsonElement>();

				string pdsUrl = didDoc.GetProperty("service")
					.EnumerateArray()
					.First(s => s.GetProperty("type").GetString() == "AtprotoPersonalDataServer")
					.GetProperty("serviceEndpoint").GetString()!;

				_logger.LogInformation($"[BlueSky] Пользователь {handle} живет на сервере: {pdsUrl}");

				var codeVerifier = GenerateRandomString(64);
				var codeChallenge = GenerateCodeChallenge(codeVerifier);
				var state = Guid.NewGuid().ToString("N");

				// === ВАЖНО: Сохраняем данные в REDIS на 15 минут, привязывая к state ===
				var cacheOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) };
				await _cache.SetStringAsync($"bsky_userId:{state}", userId, cacheOptions);
				await _cache.SetStringAsync($"bsky_verifier:{state}", codeVerifier, cacheOptions);
				await _cache.SetStringAsync($"bsky_handle:{state}", handle, cacheOptions);
				await _cache.SetStringAsync($"bsky_did:{state}", did, cacheOptions);
				await _cache.SetStringAsync($"bsky_pds:{state}", pdsUrl, cacheOptions);

				var url = $"https://bsky.social/oauth/authorize?" +
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
				_logger.LogError(ex.ToString());
				return RedirectToAction("Index");
			}
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId) // Добавили FromForm для надежности
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			var settings = await _db.BlueSkySettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				_db.BlueSkySettings.Remove(settings);
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 2. ОБРАБОТКА ОТВЕТА (CALLBACK)
		// ==========================================================
		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery] string? error_description)
		{
			_logger.LogInformation($"[BlueSky] Callback params -> Code: {code?.Length}, State: {state}");

			// Достаем данные из кэша по ключу state
			var codeVerifier = await _cache.GetStringAsync($"bsky_verifier:{state}");
			var internalUserIdStr = await _cache.GetStringAsync($"bsky_userId:{state}"); // Наш ID
			var handle = await _cache.GetStringAsync($"bsky_handle:{state}");
			var did = await _cache.GetStringAsync($"bsky_did:{state}");
			var pds = await _cache.GetStringAsync($"bsky_pds:{state}");

			if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(internalUserIdStr))
			{
				_logger.LogError("[BlueSky] Не удалось найти UserId в сессии/кеше. Возможно, прошло > 15 мин.");
				return BadRequest("Ошибка: сессия истекла.");
			}

			int internalUserId = int.Parse(internalUserIdStr);

			try
			{
				var tokenUrl = "https://bsky.social/oauth/token";
				var (dpopProof, privateKey) = _blueSkyService.CreateDPoPProof("POST", tokenUrl);

				var values = new Dictionary<string, string> {
					{ "grant_type", "authorization_code" },
					{ "code", code! },
					{ "redirect_uri", RedirectUri },
					{ "client_id", ClientId },
					{ "code_verifier", codeVerifier! }
				};

				var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(values) };
				request.Headers.Add("DPoP", dpopProof);

				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				// 2. ПРОВЕРКА НА ТРЕБОВАНИЕ NONCE
				if (!response.IsSuccessStatusCode && json.Contains("use_dpop_nonce"))
				{
					if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
					{
						var serverNonce = nonceValues.First();

						// Используем ТОТ ЖЕ ключ (privateKey), что получили в первой попытке выше
						var (newDpopProof, _) = _blueSkyService.CreateDPoPProof("POST", tokenUrl, privateKey, serverNonce);

						var retryRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(values) };
						retryRequest.Headers.Add("DPoP", newDpopProof);

						response = await _httpClient.SendAsync(retryRequest);
						json = await response.Content.ReadAsStringAsync();
					}
				}

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError($"[BlueSky] Ошибка обмена токена: {json}");
					return Content(json);
				}

				// 3. УСПЕХ! Парсим и сохраняем
				var data = JsonDocument.Parse(json).RootElement;
				int expiresIn = data.GetProperty("expires_in").GetInt32();
				var expireDate = DateTime.UtcNow.AddSeconds(expiresIn);

				await SaveToken(internalUserId,
								data.GetProperty("access_token").GetString()!,
								data.GetProperty("refresh_token").GetString()!,
								handle!, did!, privateKey, pds, expireDate);

				return RedirectToAction("Index");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка в Callback");
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

		private async Task SaveToken(int userId, string access, string refresh, string handle, string did, string privateKey, string pds, DateTime expireDate)
		{
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings == null)
			{
				settings = new BlueSkySettings { UserId = userId };
				_db.BlueSkySettings.Add(settings);
			}

			settings.AccessToken = access;
			settings.TokenExpiresAt = expireDate;
			settings.RefreshToken = refresh;
			settings.Handle = handle;
			settings.Did = did;
			settings.PdsUrl = pds;
			settings.PrivateKeyJson = privateKey; // Сохраняем ключ!
			settings.IsActive = true;

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
				application_type = "web",
				token_endpoint_auth_method = "none",

				// === ВАЖНОЕ ДОБАВЛЕНИЕ ===
				dpop_bound_access_tokens = true
			});
		}
	}


}
