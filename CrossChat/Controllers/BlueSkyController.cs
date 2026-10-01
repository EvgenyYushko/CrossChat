using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using static CrossChat.Infrastructure.Constants.AppConstants;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("bluesky")]
	public class BlueSkyController : BaseController
	{
		private readonly ILogger<BlueSkyController> _logger;
		private readonly AppDbContext _db;
		private readonly HttpClient _httpClient;
		private string ClientId => $"{APP_URL}/bluesky/client-metadata.json";
		private string RedirectUri => $"{APP_URL}/bluesky/auth/callback";
		private readonly IDistributedCache _cache;
		private readonly IBlueSkyService _blueSkyService;
		private readonly IBlueSkyTokenManager _tokenManager;
		public BlueSkyController(
			ILogger<BlueSkyController> logger,
			AppDbContext db,
			IDistributedCache cache,
			IBlueSkyService blueSkyService,
			IBlueSkyTokenManager tokenManager) // <-- ВНЕДРЯЕМ МЕНЕДЖЕР ТОКЕНОВ
		{
			_logger = logger;
			_db = db;
			_httpClient = new HttpClient();
			_cache = cache;
			_blueSkyService = blueSkyService;
			_tokenManager = tokenManager;
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК (/bluesky)
		// ==========================================================
		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.BlueSkySettings
				.Include(p => p.Profile)
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			// МЕТОД ИЗ BaseController: считаем посты для предупреждения перед удалением
			ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.BlueSky, botId);

			return View(settings);
		}

		// ==========================================================
		// 2. ОТКЛЮЧЕНИЕ АККАУНТА (С УМНОЙ ОЧИСТКОЙ ИЗ BaseController)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.BlueSkySettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 1. УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ И ФАЙЛОВ GOOGLE DRIVE В 1 СТРОКУ:
				await CleanupLinkedPostsAsync(NetworkType.BlueSky, botId);

				// 2. Удаляем сам аккаунт из БД
				_db.BlueSkySettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation("✅ [BlueSky] Бот @{Handle} и все связанные посты успешно удалены.", settings.Handle);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// 3. ПОДКЛЮЧЕНИЕ (OAUTH PKCE)
		// ==========================================================
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

				// Сохраняем параметры авторизации и ID профиля в Redis на 15 минут
				var cacheOptions = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) };
				await _cache.SetStringAsync($"bsky_userId:{state}", userId, cacheOptions);
				await _cache.SetStringAsync($"bsky_verifier:{state}", codeVerifier, cacheOptions);
				await _cache.SetStringAsync($"bsky_handle:{state}", handle, cacheOptions);
				await _cache.SetStringAsync($"bsky_did:{state}", did, cacheOptions);
				await _cache.SetStringAsync($"bsky_pds:{state}", pdsUrl, cacheOptions);

				// Безопасно сохраняем профиль:
				int currentProfileId = await GetActiveProfileIdSafeAsync(_db, int.Parse(userId));
				await _cache.SetStringAsync($"bsky_profileId:{state}", currentProfileId.ToString(), cacheOptions);

				var scope = Uri.EscapeDataString("atproto transition:generic transition:chat.bsky");

				var url = $"https://bsky.social/oauth/authorize?" +
						  $"client_id={Uri.EscapeDataString(ClientId)}&" +
						  $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
						  $"response_type=code&" +
						  $"scope={scope}&" +
						  $"state={state}&" +
						  $"code_challenge={codeChallenge}&" +
						  $"code_challenge_method=S256&" +
						  $"login_hint={handle}";

				return Redirect(url);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при старте авторизации BlueSky");
				return RedirectToAction("Index");
			}
		}

		// ==========================================================
		// 4. КОЛЛБЭК АВТОРИЗАЦИИ (CALLBACK)
		// ==========================================================
		[HttpGet("auth/callback")]
		[AllowAnonymous]
		public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery] string? error_description)
		{
			_logger.LogInformation($"[BlueSky] Callback params -> Code: {code?.Length}, State: {state}");

			var codeVerifier = await _cache.GetStringAsync($"bsky_verifier:{state}");
			var internalUserIdStr = await _cache.GetStringAsync($"bsky_userId:{state}");
			var handle = await _cache.GetStringAsync($"bsky_handle:{state}");
			var did = await _cache.GetStringAsync($"bsky_did:{state}");
			var pds = await _cache.GetStringAsync($"bsky_pds:{state}");
			var profileIdStr = await _cache.GetStringAsync($"bsky_profileId:{state}");

			if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(internalUserIdStr))
			{
				_logger.LogError("[BlueSky] Не удалось найти UserId в сессии/кеше. Время ожидания истекло.");
				return BadRequest("Ошибка: сессия авторизации истекла.");
			}

			int internalUserId = int.Parse(internalUserIdStr);
			int? profileId = int.TryParse(profileIdStr, out var pid) ? pid : null;

			try
			{
				var tokenUrl = "https://bsky.social/oauth/token";
				var (dpopProof1, privateKey) = _blueSkyService.CreateDPoPProof("POST", tokenUrl);

				var values = new Dictionary<string, string> {
					{ "grant_type", "authorization_code" },
					{ "code", code! },
					{ "redirect_uri", RedirectUri },
					{ "client_id", ClientId },
					{ "code_verifier", codeVerifier! }
				};

				var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(values) };
				request.Headers.Add("DPoP", dpopProof1);

				var response = await _httpClient.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				// Проверка на требование Nonce
				if (!response.IsSuccessStatusCode && json.Contains("use_dpop_nonce"))
				{
					if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
					{
						var serverNonce = nonceValues.First();
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

				var data = JsonDocument.Parse(json).RootElement;
				var accessToken = data.GetProperty("access_token").GetString()!;
				var refreshToken = data.GetProperty("refresh_token").GetString()!;
				int expiresIn = data.GetProperty("expires_in").GetInt32();

				// ВАЖНО: Срок жизни токена строго в UTC!
				var expireDate = DateTime.UtcNow.AddSeconds(expiresIn);

				// Подгружаем аватарку
				string? avatarUrl = null;
				try
				{
					var profileUrl = $"{pds.TrimEnd('/')}/xrpc/app.bsky.actor.getProfile?actor={did}";
					var (dpopProof, _) = _blueSkyService.CreateDPoPProof("GET", profileUrl, privateKey, null, accessToken);

					var profileRequest = new HttpRequestMessage(HttpMethod.Get, profileUrl);
					profileRequest.Headers.Add("Authorization", $"DPoP {accessToken}");
					profileRequest.Headers.Add("DPoP", dpopProof);

					var profileResp = await _httpClient.SendAsync(profileRequest);
					var profileJson = await profileResp.Content.ReadAsStringAsync();

					if (!profileResp.IsSuccessStatusCode && profileJson.Contains("use_dpop_nonce"))
					{
						if (profileResp.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
						{
							var serverNonce = nonceValues.First();
							var (retryDpopProof, _) = _blueSkyService.CreateDPoPProof("GET", profileUrl, privateKey, serverNonce, accessToken);

							var retryRequest = new HttpRequestMessage(HttpMethod.Get, profileUrl);
							retryRequest.Headers.Add("Authorization", $"DPoP {accessToken}");
							retryRequest.Headers.Add("DPoP", retryDpopProof);

							profileResp = await _httpClient.SendAsync(retryRequest);
							profileJson = await profileResp.Content.ReadAsStringAsync();
						}
					}

					if (profileResp.IsSuccessStatusCode)
					{
						using var profileDoc = JsonDocument.Parse(profileJson);
						if (profileDoc.RootElement.TryGetProperty("avatar", out var av))
						{
							avatarUrl = av.GetString();
							_logger.LogInformation("[BlueSky] Аватар успешно получен после рукопожатия!");
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось подгрузить аватарку BlueSky");
				}

				// Сохраняем токен в БД
				var settings = await SaveToken(
					internalUserId,
					accessToken,
					refreshToken,
					handle!,
					did!,
					privateKey,
					pds!,
					expireDate,
					avatarUrl,
					profileId);

				return RedirectToAction("Index", new { botId = settings.Id });
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка в Callback BlueSky");
				return RedirectToAction("Index");
			}
		}

		private async Task<BlueSkySettings> SaveToken(
			int userId,
			string access,
			string refresh,
			string handle,
			string did,
			string privateKey,
			string pds,
			DateTime expireDate,
			string? profilePicUrl,
			int? profileId = null)
		{
			var settings = await _db.BlueSkySettings
				.FirstOrDefaultAsync(s => s.UserId == userId && s.Did == did);

			bool isNew = false;
			if (settings == null)
			{
				int targetProfileId = (profileId.HasValue && profileId.Value > 0)
					? profileId.Value
					: await GetActiveProfileIdSafeAsync(_db, userId);

				settings = new BlueSkySettings
				{
					UserId = userId,
					Did = did,
					ProfileId = targetProfileId
				};
				_db.BlueSkySettings.Add(settings);
				isNew = true;
			}

			string? base64Avatar = null;
			if (!string.IsNullOrEmpty(profilePicUrl))
			{
				base64Avatar = await DownloadImageAsBase64ForHtml(profilePicUrl);
			}

			settings.AccessToken = access;
			settings.RefreshToken = refresh;
			settings.TokenExpiresAt = expireDate;
			settings.Handle = handle;
			settings.PdsUrl = pds;
			settings.PrivateKeyJson = privateKey;
			settings.IsActive = false;

			if (base64Avatar != null)
			{
				settings.ProfilePictureUrl = base64Avatar;
			}

			await _db.SaveChangesAsync();

			_logger.LogInformation(isNew
				? $"[BlueSky] Добавлен новый аккаунт @{handle}"
				: $"[BlueSky] Обновлен токен для @{handle}");

			return settings;
		}

		// ==========================================================
		// 5. СОХРАНЕНИЕ НАСТРОЕК (ПРОМПТЫ / АВТООТВЕТЫ)
		// ==========================================================
		[HttpPost("update")]
		[Authorize]
		public async Task<IActionResult> Update(
			int botId,
			string systemPrompt,
			string commentPrompt,
			int profileId,
			bool isDirectEnabled,
			bool isCommentsEnabled,
			int commentReplyMode,
			string? commentTemplates)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

			var settings = await _db.BlueSkySettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				settings.IsActive = isDirectEnabled || isCommentsEnabled;
				settings.IsDirectEnabled = isDirectEnabled;
				settings.IsCommentsEnabled = isCommentsEnabled;

				settings.SystemPrompt = systemPrompt ?? "";
				settings.CommentPrompt = commentPrompt ?? "";
				settings.CommentReplyMode = commentReplyMode > 0 ? commentReplyMode : 2;
				settings.CommentTemplates = commentTemplates;
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
				_logger.LogInformation($"[BlueSky] Настройки обновлены для @{settings.Handle}.");
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
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
				scope = "atproto transition:generic transition:chat.bsky",
				grant_types = new[] { "authorization_code", "refresh_token" },
				response_types = new[] { "code" },
				application_type = "web",
				token_endpoint_auth_method = "none",
				dpop_bound_access_tokens = true
			});
		}

		// ==========================================================
		// СТРАНИЦА АНАЛИТИКИ АККАУНТА BLUESKY (/bluesky/analytics)
		// ==========================================================
		[HttpGet("analytics")]
		public async Task<IActionResult> Analytics(int botId, string? cursor = null)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Проверяем принадлежность аккаунта пользователю
			var settings = await _db.BlueSkySettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			{
				return RedirectToAction("Index");
			}

			// Получаем гарантированно свежий токен через менеджер
			var botModel = await _tokenManager.GetValidTokenAsync(botId);
			if (botModel == null)
			{
				return RedirectToAction("Index", new { botId });
			}

			ViewBag.BotId = botId;
			ViewBag.Handle = botModel.Handle;
			ViewBag.AvatarUrl = settings.ProfilePictureUrl;

			// Параллельно запрашиваем расширенный профиль со счетчиками и ленту постов
			var profileTask = _blueSkyService.GetFullProfileAsync(botModel);
			var feedTask = _blueSkyService.GetAuthorFeedAsync(botModel, 24, cursor);

			await Task.WhenAll(profileTask, feedTask);

			var profile = await profileTask;
			var feed = await feedTask;

			var insights = new BlueSkyAccountInsightsDto
			{
				FollowersCount = profile?.FollowersCount ?? 0,
				FollowsCount = profile?.FollowsCount ?? 0,
				PostsCount = profile?.PostsCount ?? 0,
				TotalLikesOnFeed = feed.Posts.Sum(p => p.LikesCount),
				TotalRepostsOnFeed = feed.Posts.Sum(p => p.RepostsCount),
				TotalRepliesOnFeed = feed.Posts.Sum(p => p.RepliesCount),
				TotalQuotesOnFeed = feed.Posts.Sum(p => p.QuotesCount)
			};

			ViewBag.AccountInsights = insights;
			ViewBag.FollowersCount = insights.FollowersCount;

			return View(feed);
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
	}
}