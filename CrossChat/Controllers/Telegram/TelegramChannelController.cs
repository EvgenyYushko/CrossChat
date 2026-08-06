using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("telegram-channel")]
	public class TelegramChannelController : Controller
	{
		private readonly AppDbContext _db;
		private readonly IDistributedCache _cache;
		private readonly ILogger<TelegramChannelController> _logger;

		public TelegramChannelController(AppDbContext db, IDistributedCache cache, ILogger<TelegramChannelController> logger)
		{
			_db = db;
			_cache = cache;
			_logger = logger;
		}

		[HttpGet]
		public async Task<IActionResult> Index(int? botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var user = await _db.Users.FindAsync(userId);
			ViewBag.IsTgLinked = user?.TelegramUserId.HasValue ?? false;

			TelegramChannelSettings? settings = null;
			if (botId.HasValue)
			{
				settings = await _db.TelegramChannelSettings
					.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);
			}

			ViewBag.Profiles = await _db.Profile.Where(p => p.UserId == userId).ToListAsync();

			return View(settings);
		}

		// Генерация одноразового диплинка для связки Telegram аккаунта
		[HttpPost("generate-link-code")]
		public async Task<IActionResult> GenerateLinkCode()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			
			var code = Guid.NewGuid().ToString("N")[..8]; // Одноразовый код
			
			// Сохраняем связку код -> userId на 15 минут в Redis
			await _cache.SetStringAsync($"tg_link:{code}", userId.ToString(), new DistributedCacheEntryOptions
			{
				AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
			});

			var deepLink = $"https://t.me/Croshub_bot?start=link_{code}";
			return Json(new { link = deepLink });
		}

		[HttpPost("update")]
		public async Task<IActionResult> Update(int botId, string systemPrompt, int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.TelegramChannelSettings.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				settings.IsActive = isActiveRaw.Contains("true");
				settings.SystemPrompt = systemPrompt ?? "";
				settings.ProfileId = profileId;

				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		[HttpPost("disconnect")]
		public async Task<IActionResult> Disconnect(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.TelegramChannelSettings.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				_db.TelegramChannelSettings.Remove(settings);
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index");
		}
	}
}