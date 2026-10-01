using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Telegram.Bot;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("telegram-channel")]
	public class TelegramChannelController : BaseController // <-- Наследуемся от BaseController
	{
		private readonly AppDbContext _db;
		private readonly IDistributedCache _cache;
		private readonly ILogger<TelegramChannelController> _logger;
		private readonly ITelegramBotClient _botClient;
		private readonly ITelegramService _telegramService;

		public TelegramChannelController(
			AppDbContext db,
			IDistributedCache cache,
			ILogger<TelegramChannelController> logger,
			ITelegramBotClient botClient,
			ITelegramService telegramService) // <-- ДОБАВЛЕНО
		{
			_db = db;
			_cache = cache;
			_logger = logger;
			_botClient = botClient;
			_telegramService = telegramService;
		}

		// ==========================================================
		// 1. СТРАНИЦА НАСТРОЕК КАНАЛА (/telegram-channel)
		// ==========================================================
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

				// МЕТОД BaseController: считаем посты для предупреждения перед отключением
				ViewBag.LinkedPostsCount = await GetLinkedPostsCountAsync(NetworkType.TelegramChannel, botId.Value);
			}
			else
			{
				ViewBag.LinkedPostsCount = 0;
			}

			ViewBag.Profiles = await _db.Profile.Where(p => p.UserId == userId).ToListAsync();

			return View(settings);
		}

		// ==========================================================
		// 2. ГЕНЕРАЦИЯ ДИПЛИНКА ДЛЯ ПРИВЯЗКИ TELEGRAM
		// ==========================================================
		[HttpPost("generate-link-code")]
		public async Task<IActionResult> GenerateLinkCode()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var code = Guid.NewGuid().ToString("N")[..8];

			await _cache.SetStringAsync($"tg_link:{code}", userId.ToString(), new DistributedCacheEntryOptions
			{
				AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
			});

			var deepLink = $"https://t.me/Croshub_bot?start=link_{code}";
			return Json(new { link = deepLink });
		}

		// ==========================================================
		// 3. СОХРАНЕНИЕ НАСТРОЕК КАНАЛА
		// ==========================================================
		[HttpPost("update")]
		[Authorize]
		public async Task<IActionResult> Update(
			int botId,
			string systemPrompt,
			int profileId,
			bool autoApproveJoinRequests,
			bool notifyOnJoinRequests,
			bool notifyOnMemberLeft)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var channel = await _db.TelegramChannelSettings
				.FirstOrDefaultAsync(c => c.Id == botId && c.UserId == userId);

			if (channel != null)
			{
				var isActiveRaw = Request.Form["isActive"].ToString();
				channel.IsActive = isActiveRaw.Contains("true");
				channel.SystemPrompt = systemPrompt ?? "";
				channel.ProfileId = profileId;

				channel.AutoApproveJoinRequests = autoApproveJoinRequests;
				channel.NotifyOnJoinRequests = notifyOnJoinRequests;
				channel.NotifyOnMemberLeft = notifyOnMemberLeft;

				await _db.SaveChangesAsync();
				_logger.LogInformation("✅ [Telegram Channel] Настройки канала '{Title}' обновлены.", channel.ChannelTitle);
			}

			return RedirectToAction("Index", new { botId = botId, saved = "true" });
		}

		// ==========================================================
		// 4. ОТКЛЮЧЕНИЕ КАНАЛА (С УМНОЙ ОЧИСТКОЙ ПОСТОВ ИЗ BaseController)
		// ==========================================================
		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect([FromForm] int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var settings = await _db.TelegramChannelSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				// 1. Умная очистка публикаций и файлов Google Drive:
				await CleanupLinkedPostsAsync(NetworkType.TelegramChannel, botId);

				// 2. Бот вежливо выходит из канала в самом Telegram (если может)
				try
				{
					await _botClient.LeaveChat(settings.ChannelId);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось выполнить LeaveChat для канала {ChannelId}", settings.ChannelId);
				}

				// 3. Удаляем канал из БД
				_db.TelegramChannelSettings.Remove(settings);
				await _db.SaveChangesAsync();

				_logger.LogInformation("✅ [Telegram Channel] Канал '{Title}' успешно отключен с сайта.", settings.ChannelTitle);
			}

			return RedirectToAction("Profile", "Auth");
		}

		// ==========================================================
		// СТРАНИЦА АНАЛИТИКИ TELEGRAM КАНАЛА (/telegram-channel/analytics)
		// ==========================================================
		[HttpGet("analytics")]
		public async Task<IActionResult> Analytics(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Проверяем принадлежность канала пользователю
			var channel = await _db.TelegramChannelSettings
				.Include(c => c.Profile)
				.FirstOrDefaultAsync(c => c.Id == botId && c.UserId == userId);

			if (channel == null) return RedirectToAction("Index");

			// 1. ВСЕГДА получаем живое число подписчиков прямо из Telegram Bot API!
			int liveSubscribers = await _telegramService.GetChatMemberCountAsync(channel.ChannelId);

			var model = new TelegramChannelAnalyticsPageDto
			{
				Stats = new TelegramChannelStatsDto
				{
					ChannelId = channel.ChannelId,
					ChannelTitle = channel.ChannelTitle,
					ChannelUsername = channel.ChannelUsername,
					ProfilePictureUrl = channel.ProfilePictureUrl,
					SubscribersCount = liveSubscribers,
					AutoApproveJoinRequests = channel.AutoApproveJoinRequests
				}
			};

			// 2. ЕСЛИ КАНАЛ ПУБЛИЧНЫЙ — ПАРСИМ РЕАЛЬНЫЕ ПОСТЫ, ПРОСМОТРЫ И РЕАКЦИИ
			if (!string.IsNullOrEmpty(channel.ChannelUsername))
			{
				var posts = await _telegramService.GetPublicChannelPostsAsync(channel.ChannelUsername, liveSubscribers);
				model.Posts = posts;

				if (posts.Any())
				{
					model.Stats.TotalViewsOnFeed = posts.Sum(p => p.Views);
					model.Stats.TotalReactionsOnFeed = posts.Sum(p => p.Reactions);

					var validEr = posts.Where(p => p.EngagementRate > 0).ToList();
					model.Stats.AverageEr = validEr.Any() ? Math.Round(validEr.Average(p => p.EngagementRate), 1) : 0;
				}
			}

			ViewBag.BotId = botId;
			return View(model);
		}
	}
}