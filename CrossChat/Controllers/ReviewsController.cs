using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types.ReplyMarkups;
using static CrossChat.Helpers.TimeZoneHelper;
using static CrossChat.Constants.AppConstants;

namespace CrossChat.Controllers
{
	[Route("reviews")]
	public class ReviewsController : Controller
	{
		private readonly AppDbContext _db;
		private readonly ILogger<ReviewsController> _logger;
		private readonly ITelegramService _telegramService;
		private readonly IEmailService _emailService;

		public ReviewsController(AppDbContext db, ILogger<ReviewsController> logger, ITelegramService telegramService, IEmailService emailService)
		{
			_db = db;
			_logger = logger;
			_telegramService = telegramService;
			_emailService = emailService;
		}

		[HttpGet]
		public async Task<IActionResult> Index()
		{
			// Достаем все отзывы, отсортированные от новых к старым
			var reviews = await _db.Reviews
				.Include(r => r.User)
				.OrderByDescending(r => r.CreatedAt)
				.ToListAsync();

			// Получаем ID текущего юзера
			int? currentUserId = null;
			if (User.Identity?.IsAuthenticated == true && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid))
			{
				currentUserId = uid;
			}

			ViewBag.CurrentUserId = currentUserId;
			return View(reviews);
		}

		// 1. СОЗДАНИЕ НОВОГО ОТЗЫВА
		[HttpPost("add")]
		[Authorize]
		public async Task<IActionResult> AddReview([FromForm] int rating, [FromForm] string comment)
		{
			// Нормализуем переносы строк (\r\n -> \n), чтобы подсчет символов строго совпадал с JS
			var normalizedComment = comment?.Replace("\r\n", "\n").Trim() ?? "";

			// Проверка диапазона оценок и длины текста (от 10 до 500 символов)
			if (rating < 1 || rating > 5 || normalizedComment.Length < 10 || normalizedComment.Length > 500)
			{
				TempData["Error"] = "Длина отзыва должна составлять от 10 до 500 символов, а оценка — от 1 до 5 звезд.";
				TempData["PreservedComment"] = comment; // Сохраняем введенный текст, чтобы не стереть его!
				return RedirectToAction("Index");
			}

			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

			var review = new ReviewEntity
			{
				UserId = userId,
				Rating = rating,
				Comment = normalizedComment,
				CreatedAt = DateTimeNow
			};

			_db.Reviews.Add(review);
			await _db.SaveChangesAsync();

			// ---ОТПРАВКА УВЕДОМЛЕНИЯ АДМИНИСТРАТОРУ В TELEGRAM ---
			try
			{
				string stars = new string('⭐', rating);
				string userName = User.FindFirstValue(ClaimTypes.Name) ?? $"Пользователь #{userId}";
				string userEmail = User.FindFirstValue(ClaimTypes.Email) ?? "";

				string adminMessage =
					$"🌟 <b>НОВЫЙ ОТЗЫВ НА САЙТЕ!</b>\n\n" +
					$"<b>Автор:</b> {userName} {(string.IsNullOrEmpty(userEmail) ? "" : $"({userEmail})")}\n" +
					$"<b>Оценка:</b> {stars} ({rating}/5)\n" +
					$"<b>Дата:</b> {DateTimeNow:dd.MM.yyyy HH:mm}\n\n" +
					$"<b>Текст отзыва:</b>\n" +
					$"<i>«{normalizedComment}»</i>";

				// Создаем кнопку-ссылку прямо на страницу отзывов вашего сайта
				var inlineKeyboard = new InlineKeyboardMarkup(new[]
				{
					InlineKeyboardButton.WithUrl("🌐 Открыть отзывы на сайте", $"{APP_URL}/reviews")
				});

				// Передаем кнопку в метод SendMessageToAdmin
				await _telegramService.SendMessageToAdmin(adminMessage, replyMarkup: inlineKeyboard);

				await _emailService.SendFromNoReplyAsync("jeka-krut@mail.ru", "Test send comment", adminMessage);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Reviews] Не удалось отправить уведомление о новом отзыве в Telegram админу");
			}


			TempData["Success"] = "Спасибо! Ваш отзыв успешно опубликован.";
			return RedirectToAction("Index");
		}

		// 2. ИЗМЕНЕНИЕ СУЩЕСТВУЮЩЕГО ОТЗЫВА ПО ЕГО ID
		[HttpPost("update/{id}")]
		[Authorize]
		public async Task<IActionResult> UpdateReview(int id, [FromForm] int rating, [FromForm] string comment)
		{
			var normalizedComment = comment?.Replace("\r\n", "\n").Trim() ?? "";

			if (rating < 1 || rating > 5 || normalizedComment.Length < 10 || normalizedComment.Length > 500)
			{
				TempData["Error"] = "Длина отзыва должна составлять от 10 до 500 символов, а оценка — от 1 до 5 звезд.";
				TempData["PreservedComment"] = comment; // Сохраняем введенный текст
				return RedirectToAction("Index");
			}

			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

			var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

			if (review == null)
			{
				TempData["Error"] = "Отзыв не найден или у вас нет прав на его редактирование.";
				return RedirectToAction("Index");
			}

			review.Rating = rating;
			review.Comment = normalizedComment;
			review.CreatedAt = DateTimeNow;

			await _db.SaveChangesAsync();
			TempData["Success"] = "Ваш отзыв успешно обновлен.";

			return RedirectToAction("Index");
		}

		// 3. УДАЛЕНИЕ СВОЕГО ОТЗЫВА ПО ЕГО ID
		[HttpPost("delete/{id}")]
		[Authorize]
		public async Task<IActionResult> DeleteReview(int id)
		{
			var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
			if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

			// Ищем отзыв в БД и проверяем, что он принадлежит именно текущему юзеру
			var review = await _db.Reviews.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

			if (review == null)
			{
				TempData["Error"] = "Отзыв не найден или у вас нет прав на его удаление.";
				return RedirectToAction("Index");
			}

			// Удаляем из базы данных
			_db.Reviews.Remove(review);
			await _db.SaveChangesAsync();

			_logger.LogInformation($"[Reviews] Пользователь {userId} удалил свой отзыв #{id}.");
			TempData["Success"] = "Ваш отзыв успешно удален.";

			return RedirectToAction("Index");
		}
	}
}