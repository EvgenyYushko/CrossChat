using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Route("telegram")]
	public class TelegramController : Controller
	{
		private readonly IPublishEndpoint _publish;

		private readonly AppDbContext _db;
		private readonly ITelegramService _telegramService;
		private readonly ILogger<TelegramController> _logger;

		public TelegramController(AppDbContext db, ITelegramService telegramService, ILogger<TelegramController> logger, IPublishEndpoint publish)
		{
			_db = db;
			_telegramService = telegramService;
			_logger = logger;
			_publish = publish;
		}


		[HttpGet]
		public async Task<IActionResult> Index()
		{
			if (!User.Identity.IsAuthenticated) return RedirectToAction("Login", "Auth");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			// 1. Достаем настройки из базы
			var settings = await _db.TelegramSettings
				.FirstOrDefaultAsync(s => s.UserId == userId);

			return View(settings);
		}

		[HttpPost("connect")]
		[Authorize]
		public async Task<IActionResult> Connect(string token)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));

			try
			{
				// 1. Проверяем валидность токена через getMe
				var botClient = new TelegramBotClient(token);
				var me = await botClient.GetMe();

				// 2. Ищем существующие настройки в БД
				var settings = await _db.TelegramSettings.FirstOrDefaultAsync(s => s.UserId == userId);

				bool isNew = false;
				if (settings == null)
				{
					// Если в базе нет, создаем новый объект
					settings = new TelegramSettings { UserId = userId };
					isNew = true;
				}

				// 3. Подписываемся на вебхуки
				var webhookUrl = $"{APP_URL}/telegram/webhook/{token}";
				await _telegramService.SetWebhookAsync(token, webhookUrl);

				// 4. Сохраняем в БД
				settings.BotToken = token;
				settings.BotUsername = me.Username;
				settings.IsActive = true;

				// Если запись была новая - добавляем в трекер EF
				if (isNew)
				{
					_db.TelegramSettings.Add(settings);
				}
				// Если запись существовала - EF Core автоматически увидит изменения 
				// (Update не обязателен, если объект загружен из этого же контекста)

				await _db.SaveChangesAsync();

				_logger.LogInformation($"[Telegram] Бот @{me.Username} успешно подключен пользователем {userId}");
				return RedirectToAction("Index", "Telegram");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при подключении Telegram бота");
				// Можно добавить TempData["Error"] = "Неверный токен или ошибка API";
				return RedirectToAction("Index", "Telegram");
			}
		}

		[HttpPost("disconnect")]
		[Authorize]
		public async Task<IActionResult> Disconnect()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.TelegramSettings.FirstOrDefaultAsync(s => s.UserId == userId);

			if (settings != null && !string.IsNullOrEmpty(settings.BotToken))
			{
				try
				{
					// Отписываемся от вебхуков
					await _telegramService.DeleteWebhookAsync(settings.BotToken);
				}
				catch (Exception ex)
				{
					_logger.LogWarning($"[Telegram] Не удалось отписать вебхук: {ex.Message}");
				}

				// Очищаем БД
				settings.BotToken = null;
				settings.BotUsername = null;
				settings.IsActive = false;

				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index", "Telegram");
		}

		[HttpPost("update")]
		[Authorize]
		public async Task<IActionResult> Update(string systemPrompt)
		{
			// Явно читаем чекбокс из формы
			// Если "isActive" есть в форме - значит true, иначе false
			var isActiveRaw = Request.Form["isActive"].ToString();

			// Если строка содержит "true" — значит галочка была включена
			bool isActive = isActiveRaw.Contains("true");
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier));
			var settings = await _db.TelegramSettings.FirstOrDefaultAsync(s => s.UserId == userId);


			if (settings != null)
			{
				// Если статус активности изменился (например, пользователь включил/выключил бота)
				if (settings.IsActive != isActive)
				{
					if (isActive)
					{
						var webhookUrl = $"{APP_URL}/telegram/webhook/{settings.BotToken}";
						await _telegramService.SetWebhookAsync(settings.BotToken, webhookUrl);
					}
					else
					{
						await _telegramService.DeleteWebhookAsync(settings.BotToken);
					}
					settings.IsActive = isActive;
				}

				settings.SystemPrompt = systemPrompt ?? "";
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index", "Telegram", new { saved = "true" });
		}

		[HttpPost("webhook/{token}")]
		public async Task<IActionResult> Receive(string token, [FromBody] Update update)
		{
			//if (token != settings.BotToken)

			_logger.LogInformation("Получено сообщение от телеги");
			// ВАЖНО: token в пути — это наш способ понять, чей это бот
			if (update.Type == UpdateType.Message && update.Message?.Text != null)
			{
				await _publish.Publish(new TelegramMessageReceived
				{
					BotToken = token,
					ChatId = update.Message.Chat.Id,
					Text = update.Message.Text
				});
			}
			return Ok();
		}

	}
}
