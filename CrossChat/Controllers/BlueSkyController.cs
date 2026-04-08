using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
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

		public BlueSkyController(ILogger<BlueSkyController> logger, AppDbContext db, IDistributedCache cache)
		{
			_logger = logger;
			_db = db;
			_httpClient = new HttpClient();
			_cache = cache; // Используем кеш вместо сессии
		}

		[HttpGet]
		public async Task<IActionResult> Index()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);
			return View(settings);
		}

		[HttpGet("test-api")]
		public async Task<IActionResult> TestApi()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken) || string.IsNullOrEmpty(settings.PdsUrl))
				return Content("Данные или PDS URL не найдены. Переподключите аккаунт.");

			// ИСПОЛЬЗУЕМ СОХРАНЕННЫЙ PDS URL
			var apiUrl = $"{settings.PdsUrl.TrimEnd('/')}/xrpc/com.atproto.repo.createRecord";

			try
			{
				// Данные поста
				var payload = new
				{
					repo = settings.Did,
					collection = "app.bsky.feed.post",
					record = new
					{
						text = "Теперь я шлю посты прямо на свой PDS! 🚀 #atproto #CrossChat",
						createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
					}
				};

				// 1. Генерируем DPoP для POST запроса
				var (dpopProof, _) = CreateDPoPProof("POST", apiUrl, settings.PrivateKeyJson);

				// 2. Формируем запрос
				var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
				{
					Content = JsonContent.Create(payload)
				};
				request.Headers.Add("Authorization", $"DPoP {settings.AccessToken}");
				request.Headers.Add("DPoP", dpopProof);

				// 3. Отправляем
				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					return Content($"УРА! Пост опубликован. Ответ API: {json}");
				}
				else if (json.Contains("use_dpop_nonce"))
				{
					// Если сервер требует Nonce, нужно повторить запрос (как мы делали в Callback)
					// Для теста просто посмотри лог, но в Воркере это нужно будет реализовать
					return Content("Сервер запросил Nonce. Нужно реализовать логику повтора.");
				}

				return Content($"Ошибка: {response.StatusCode} - {json}");
			}
			catch (Exception ex)
			{
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
				var (dpopProof, privateKey) = CreateDPoPProof("POST", tokenUrl);

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
						var (newDpopProof, _) = CreateDPoPProof("POST", tokenUrl, privateKey, serverNonce);

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
				await SaveToken(internalUserId,
								data.GetProperty("access_token").GetString()!,
								data.GetProperty("refresh_token").GetString()!,
								handle!, did!, privateKey, pds);

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

		private async Task SaveToken(int userId, string access, string refresh, string handle, string did, string privateKey, string pds)
		{
			var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings == null)
			{
				settings = new BlueSkySettings { UserId = userId };
				_db.BlueSkySettings.Add(settings);
			}

			settings.AccessToken = access;
			settings.RefreshToken = refresh;
			settings.Handle = handle;
			settings.Did = did;
			settings.PdsUrl = pds;
			settings.PrivateKeyJson = privateKey; // Сохраняем ключ!
			settings.IsActive = true;

			await _db.SaveChangesAsync();
		}

		private (string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null)
		{
			ECDsa ecdsa;

			if (string.IsNullOrEmpty(existingKeyJson))
			{
				// Создаем новый ключ
				ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			}
			else
			{
				// Восстанавливаем ключ из нашего DTO
				var keyDto = JsonSerializer.Deserialize<BlueSkyKeyDto>(existingKeyJson);

				// ВАЖНО: Координаты X и Y передаются через структуру ECPoint в поле Q
				var params_ = new ECParameters
				{
					Curve = ECCurve.NamedCurves.nistP256,
					D = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto!.D),
					Q = new ECPoint
					{
						X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto.X),
						Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(keyDto.Y)
					}
				};
				ecdsa = ECDsa.Create(params_);
			}

			var signingKey = new ECDsaSecurityKey(ecdsa);
			var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(signingKey);

			// Публичная часть для заголовка (только X и Y)
			var publicJwkDict = new Dictionary<string, object> {
				{ "kty", "EC" }, { "crv", "P-256" }, { "x", jwk.X }, { "y", jwk.Y }, { "alg", "ES256" }
			};

			var handler = new JwtSecurityTokenHandler();
			var header = new JwtHeader(new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256));
			header["typ"] = "dpop+jwt";
			header["jwk"] = publicJwkDict;

			var payload = new JwtPayload {
				{ "jti", Guid.NewGuid().ToString("N") },
				{ "htm", method.ToUpper() },
				{ "htu", url },
				{ "iat", EpochTime.GetIntDate(DateTime.UtcNow) }
			};

			if (!string.IsNullOrEmpty(nonce)) payload["nonce"] = nonce;

			var token = new JwtSecurityToken(header, payload);
			var proof = handler.WriteToken(token);

			// Экспортируем параметры в наш DTO для сохранения
			var p = ecdsa.ExportParameters(true);
			var exportDto = new BlueSkyKeyDto
			{
				X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Q.X),
				Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Q.Y),
				D = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.D)
			};
			var fullKeyJson = JsonSerializer.Serialize(exportDto);

			return (proof, fullKeyJson);
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

	public class BlueSkyKeyDto
	{
		public string? X { get; set; }
		public string? Y { get; set; }
		public string? D { get; set; }
	}
}
