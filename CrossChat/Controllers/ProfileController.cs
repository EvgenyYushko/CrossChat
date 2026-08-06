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
				.Include(p => p.TelegramChannelSettingsList)
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
		[Authorize]
		public IActionResult Create() => View();

		[HttpPost("create")]
		[Authorize]
		public async Task<IActionResult> Create([FromForm]string name, IFormFile? avatarFile)
		{
			_logger.LogInformation($"[CreateProfile] Получено имя: '{name}', Размер файла: {avatarFile?.Length ?? 0}");

			if (string.IsNullOrWhiteSpace(name))
			{
				TempData["Error"] = "Имя профиля обязательно!";
				return View(); // Возвращаем форму обратно с ошибкой
			}

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var newProfile = new Profile
			{
				UserId = userId,
				Name = name,
				AvatarUrl = null
			};

			if (avatarFile != null)
			{
				newProfile.AvatarUrl = await ProcessUploadedFileToBase64(avatarFile);
			}

			_db.Profile.Add(newProfile);

			await _db.SaveChangesAsync();

			return Redirect("/profiles");
		}

		[HttpPost("delete")]
		[Authorize]
		public async Task<IActionResult> Delete(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Подгружаем ВСЕ коллекции, чтобы проверить, пуст ли профиль
			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.BlueSkySettingsList)
				.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile == null) return NotFound();

			// Проверка на наличие подключенных аккаунтов
			bool hasBots = (profile.InstagramSettingsList.Any() ||
							profile.ThreadsSettingsList.Any() ||
							profile.XSettingsList.Any() ||
							profile.FacebookSettingsList.Any() ||
							profile.TelegramUserBotSettingsList.Any() ||
							profile.TelegramSettings != null ||
							profile.BlueSkySettingsList.Any());

			if (hasBots)
			{
				TempData["Error"] = "Сначала удалите все подключенные соцсети из профиля.";
				return Redirect("/profiles");
			}

			_db.Profile.Remove(profile);
			await _db.SaveChangesAsync();
			return Redirect("/profiles");
		}

		[HttpGet("edit/{id}")]
		public async Task<IActionResult> Edit(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var profile = await _db.Profile.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile == null) return NotFound();

			// Возвращаем вьюху "Create", но передаем в нее данные профиля
			return View("Create", profile);
		}

		[HttpPost("edit/{id}")]
		public async Task<IActionResult> Edit(int id, string name, IFormFile? avatarFile)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var profile = await _db.Profile.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

			if (profile != null)
			{
				profile.Name = name;
				if (avatarFile != null)
				{
					profile.AvatarUrl = await ProcessUploadedFileToBase64(avatarFile);
				}
				await _db.SaveChangesAsync();
			}
			return Redirect("/profiles");
		}

		private async Task<string?> ProcessUploadedFileToBase64(IFormFile file)
		{
			if (file == null || file.Length == 0) return null;

			using var ms = new MemoryStream();
			await file.CopyToAsync(ms);
			var bytes = ms.ToArray();

			// Формируем формат Data URI
			var base64String = Convert.ToBase64String(bytes);
			return $"data:{file.ContentType};base64,{base64String}";
		}
	}
}
