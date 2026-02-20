using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers;

[Route("auth")]
public class AuthController : Controller
{
	private readonly AppDbContext _db;

	public AuthController(AppDbContext db)
	{
		_db = db;
	}

	// 1. Нажатие на кнопку "Войти через Google"
	[HttpGet("login")]
	public IActionResult Login()
	{
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
				AvatarUrl = avatarUrl,
				CreatedAt = DateTime.UtcNow,
				// Сразу создаем пустые настройки инсты
				InstagramSettings = new InstagramSettings()
			};
			_db.Users.Add(user);
			await _db.SaveChangesAsync();
		}
		else
		{
			if (user.AvatarUrl != avatarUrl)
			{
				user.AvatarUrl = avatarUrl;
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
			ExpiresUtc = DateTime.UtcNow.AddDays(7)
		};

		// Перезаписываем временную куку Гугла на нашу постоянную
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(claimsIdentity),
			authProperties);

		// Редирект в личный кабинет
		return RedirectToAction("Profile");
	}

	[HttpPost("logout")]
	public async Task<IActionResult> Logout()
	{
		await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
		return RedirectToAction("Index", "Home");
	}

	// Страница профиля (пока заглушка)
	[HttpGet("profile")]
	[Authorize] // Только для вошедших
	public async Task<IActionResult> Profile()
	{
		// Получаем ID текущего юзера из куки
		var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

		var user = await _db.Users
			.Include(u => u.InstagramSettings)
			.FirstOrDefaultAsync(u => u.Id == userId);

		return View(user); // Или вернуть JSON, если фронт на React/Vue
	}

	[HttpPost("update-settings")]
	[Authorize]
	public async Task<IActionResult> UpdateSettings(string systemPrompt, bool isActive)
	{
		var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

		// Загружаем юзера вместе с настройками
		var user = await _db.Users
			.Include(u => u.InstagramSettings)
			.FirstOrDefaultAsync(u => u.Id == userId);

		if (user == null) return RedirectToAction("Index", "Home");

		// Если настроек еще нет (вдруг) - создаем
		if (user.InstagramSettings == null)
		{
			user.InstagramSettings = new InstagramSettings { UserId = user.Id };
		}

		// Обновляем поля
		user.InstagramSettings.SystemPrompt = systemPrompt;
		user.InstagramSettings.IsActive = isActive;

		await _db.SaveChangesAsync();

		return RedirectToAction("Profile");
	}
}