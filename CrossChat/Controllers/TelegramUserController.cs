using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Helpers;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TL;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("telegram-user")]
	public class TelegramUserController : Controller
	{
		private readonly AppDbContext _db;
		private readonly ILogger<TelegramUserController> _logger;

		private ITelegramUserBotService _telegramUserBotService { get; }

		public TelegramUserController(AppDbContext db, ITelegramUserBotService telegramUserBotService, ILogger<TelegramUserController> logger)
		{
			_db = db;
			_telegramUserBotService = telegramUserBotService;
			_logger = logger;
		}


		[HttpGet]
		public async Task<IActionResult> Index(int botId)
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// 1. Достаем настройки из базы
			var settings = await _db.TelegramUsersBotSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);


			return View(settings);
		}

		[HttpPost("connect")]
		public async Task<IActionResult> Connect(UserBotDto input)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// 1. Создаем запись в БД в состоянии "Offline", чтобы получить ID
			var dbEntry = new TelegramUserBotSettings
			{
				UserId = userId,
				DcId = input.DcId,
				AuthKey = input.AuthKey,
				TgUserId = input.TgUserId,
				ProxyHost = input.ProxyHost,
				ProxyPort = input.ProxyPort, // Добавил порт
				ProxyUser = input.ProxyUser,
				ProxyPass = input.ProxyPass,
				TgUserName = input.TgUserName ?? "",
				SystemPrompt = "Ты вежливый ассистент.",
				IsActive = true
			};

			_db.TelegramUsersBotSettings.Add(dbEntry);
			await _db.SaveChangesAsync();

			try
			{
				// 2. Обновляем DTO, чтобы сервис знал ID для имени файла
				input.Id = dbEntry.Id;

				// 3. Запускаем через сервис (делаем Inject и Connect)
				var client = await _telegramUserBotService.CreateAndConnectAsync(input);
				var users = await client.Users_GetUsers(TL.InputUser.Self);

				// 4. Принудительно сохраняем файл сессии
				UserBotHelper.ForceSaveSession(client);

				client.Dispose(); 

				await Task.Delay(100); 
				// 5. Вычитываем байты файла и сохраняем в БД
				dbEntry.SessionData = await _telegramUserBotService.GetSessionBytesAsync(dbEntry.Id);
				dbEntry.TgUserName = users.FirstOrDefault().MainUsername ?? "";
				await _db.SaveChangesAsync();

				_logger.LogInformation($"[UserBot] Аккаунт {dbEntry.TgUserId} успешно подключен и сессия сохранена.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[UserBot] Ошибка при первой инициализации. Удаляем битую запись.");
				_db.TelegramUsersBotSettings.Remove(dbEntry);
				await _db.SaveChangesAsync();
				// Можно вернуть ошибку на фронт через TempData
				return RedirectToAction("Index", new { error = "connection_failed" });
			}

			return RedirectToAction("Index", new { botId = input.Id });

		}

		[HttpPost("update")]
		[Authorize]
		public async Task<IActionResult> Update(int botId, string systemPrompt, string ProxyHost, int? ProxyPort, string ProxyUser, string ProxyPass)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Обработка чекбокса (наш хак "false,true")
			var isActiveRaw = Request.Form["isActive"].ToString();
			bool isActive = isActiveRaw.Contains("true");

			var settings = await _db.TelegramUsersBotSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings == null) return NotFound();

			// Обновляем поля
			settings.SystemPrompt = systemPrompt;
			settings.IsActive = isActive;
			settings.ProxyHost = ProxyHost;
			settings.ProxyPort = ProxyPort;
			settings.ProxyUser = ProxyUser;
			settings.ProxyPass = ProxyPass;

			await _db.SaveChangesAsync();

			_logger.LogInformation($"[UserBot] Настройки для бота {botId} обновлены пользователем.");

			return RedirectToAction("Index", new { saved = "true", botId = botId });
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect(int botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var settings = await _db.TelegramUsersBotSettings
				.FirstOrDefaultAsync(s => s.Id == botId && s.UserId == userId);

			if (settings != null)
			{
				_db.TelegramUsersBotSettings.Remove(settings);
				await _db.SaveChangesAsync();

				// Пытаемся удалить файл сессии с диска
				string path = $"userbot_{botId}.session";
				if (System.IO.File.Exists(path))
				{
					try { System.IO.File.Delete(path); } catch { }
				}

				_logger.LogInformation($"[UserBot] Аккаунт {botId} полностью удален.");
			}

			return RedirectToAction("Index");
		}
	}
}
