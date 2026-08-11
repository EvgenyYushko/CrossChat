using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using static CrossChat.Helpers.TimeZoneHelper;
using static CrossChat.Integrations.Helpers.HttpHelper;

namespace CrossChat.Controllers;

[Route("auth")]
public class AuthController : Controller
{
	private readonly AppDbContext _db;
	private readonly ILogger<AuthController> _logger;
	private readonly IEmailService _emailService;
	IDatabase _redis;

	public AuthController(AppDbContext db, ILogger<AuthController> logger, IConnectionMultiplexer redisMux, IEmailService emailService)
	{
		_db = db;
		_logger = logger;
		_emailService = emailService;
		_redis = redisMux.GetDatabase();
	}

	[HttpGet("console")]
	[Authorize]
	public IActionResult ConsolePage([FromQuery] string provider, [FromQuery] int botId, [FromQuery] string username)
	{
		// Передаем параметры во вьюху через ViewBag
		ViewBag.Provider = provider;
		ViewBag.BotId = botId;
		ViewBag.UserName = username;
		return View("Console");
	}

	[HttpGet("console/history")]
	[Authorize]
	public async Task<IActionResult> GetConsoleHistory([FromQuery] string provider, [FromQuery] int botId)
	{
		// 1. Получаем UserId из клеймов
		var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
		if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

		// Приводим к нижнему регистру 
		var providerKey = provider?.ToLower();
		bool isOwner = false;

		// 2. ПРОВЕРКА ВЛАДЕНИЯ
		if (providerKey == "instagram")
			isOwner = await _db.InstagramSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "telegramchannel")
			isOwner = await _db.TelegramChannelSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "telegram")
			isOwner = await _db.TelegramUsersBotSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "threads")
			isOwner = await _db.ThreadsSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "bluesky")
			isOwner = await _db.BlueSkySettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "facebook")
			isOwner = await _db.FacebookSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
		else if (providerKey == "x")
			isOwner = await _db.XSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);

		if (!isOwner)
		{
			return Forbid();
		}

		// 3. ПОЛУЧЕНИЕ ДАННЫХ ИЗ REDIS
		var historyKey = $"log_history:{providerKey}:{botId}";

		// Получаем записи (последние 100)
		var logs = await _redis.ListRangeAsync(historyKey, 0, -1);

		return Ok(logs.Select(x => x.ToString()));
	}

	// 1. Нажатие на кнопку "Войти через Google"
	[HttpGet("login")]
	public async Task<IActionResult> Login()
	{
		// 1. Принудительно очищаем куки перед новой попыткой
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

		var properties = new AuthenticationProperties
		{
			RedirectUri = Url.Action("GoogleResponse")
		};
		// Отправляем пользователя на сайт Google
		return Challenge(properties, GoogleDefaults.AuthenticationScheme);
	}

	// 2. Гугл возвращает пользователя сюда
	[HttpGet("google-response")]
	public async Task<IActionResult> GoogleResponse()
	{
		// Получаем данные, которые прислал Google (во временной куке)
		var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

		// Если что-то пошло не так (отказ в доступе)
		if (!result.Succeeded) return RedirectToAction("Index", "Home");

		// Вытаскиваем данные
		var claims = result.Principal.Identities.FirstOrDefault()?.Claims;
		var googleId = claims?.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
		var email = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
		var name = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;
		var avatarUrl = claims?.FirstOrDefault(c => c.Type == "urn:google:picture")?.Value;

		var base64Avatar = await DownloadImageAsBase64(avatarUrl);

		if (string.IsNullOrEmpty(googleId) || string.IsNullOrEmpty(email))
		{
			return RedirectToAction("Index", "Home"); // Ошибка данных
		}

		// --- ЛОГИКА РЕГИСТРАЦИИ / ВХОДА ---

		// Ищем пользователя в БД
		var user = await _db.Users.FirstOrDefaultAsync(u => u.GoogleId == googleId);

		if (user == null)
		{
			// РЕГИСТРАЦИЯ: Если нет - создаем
			user = new User
			{
				GoogleId = googleId,
				Email = email,
				Name = name ?? "User",
				AvatarUrl = base64Avatar ?? avatarUrl,
				CreatedAt = DateTimeNow
			};
			_db.Users.Add(user);
			await _db.SaveChangesAsync();

			var loginUrl = "https://crosschat.ru/profiles";
			var userName = user.Name;
			var userEmail = user.Email;

			await _emailService.SendWelcomeEmailAsync(
				userName,
				userEmail,
				loginUrl
			);
		}
		else
		{
			if (user.AvatarUrl != base64Avatar)
			{
				user.AvatarUrl = base64Avatar ?? avatarUrl;
				await _db.SaveChangesAsync();
			}
		}

		// ВХОД: Создаем нашу собственную куку сессии
		// Нам нужно записать в куку ID нашего пользователя (user.Id), а не GoogleId
		var sessionClaims = new List<Claim>
		{
			new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), // Наш ID (int)
            new Claim(ClaimTypes.Name, user.Name),
			new Claim(ClaimTypes.Email, user.Email)
		};

		var claimsIdentity = new ClaimsIdentity(sessionClaims, CookieAuthenticationDefaults.AuthenticationScheme);
		var authProperties = new AuthenticationProperties
		{
			IsPersistent = true, // Запомнить меня
			ExpiresUtc = DateTimeNow.AddDays(7)
		};

		// Перезаписываем временную куку Гугла на нашу постоянную
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(claimsIdentity),
			authProperties);

		// Редирект в личный кабинет
		return Redirect("/profiles");
	}

	[HttpPost("logout")]
	public async Task<IActionResult> Logout()
	{
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return RedirectToAction("Index", "Home");
	}
}