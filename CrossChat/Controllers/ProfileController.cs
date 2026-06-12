using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	public class AuthUser
	{
		public User User { get; set; }
		public List<Profile> Profiles = new List<Profile>();
	}

	[Authorize]
	[Route("profile")]
	public class ProfileController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly ILogger<ProfileController> _logger;

		public ProfileController(AppDbContext db, ILogger<ProfileController> logger)
		{
			_db = db;
			_logger = logger;
		}

		// Страница профиля (пока заглушка)
		[HttpGet]
		public async Task<IActionResult> Index(int profileId)
		{
			// Безопасно пытаемся достать ID
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);

			// Если ID нет или он не число (битая кука)
			if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
			{
				// ЭВАКУАЦИЯ: Чистим куки и шлем на вход
				await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
				return RedirectToAction("Index", "Home");
			}

			// Тянем профиль и ВСЕ его настройки одним махом
			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.BlueSkySettingsList)
				.FirstOrDefaultAsync(p => p.Id == profileId && p.UserId == userId);

			// Если профиль не найден или чужой — 404 или редирект
			if (profile == null)
			{
				_logger.LogWarning($"Пользователь {userId} пытался открыть чужой или несуществующий профиль {profileId}");
				return NotFound();
			}

			SetActiveProfileId(profileId);

			return View(profile);
		}

		[HttpGet("create")]
		public IActionResult Create() => View();

		[HttpPost("create")]
		public async Task<IActionResult> Create(string name)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var newProfile = new Profile
			{
				UserId = userId,
				Name = name,
				AvatarUrl = null // Можно добавить логику загрузки фото позже
			};

			_db.Profile.Add(newProfile);

			await _db.SaveChangesAsync();

			return Redirect("/profiles");
		}
	}
}
