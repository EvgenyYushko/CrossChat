using System.Security.Claims;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("inbox")]
	public class InboxController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly IInboxService _inboxService;
		private readonly ILogger<InboxController> _logger;

		public InboxController(AppDbContext db, IInboxService inboxService, ILogger<InboxController> logger)
		{
			_db = db;
			_inboxService = inboxService;
			_logger = logger;
		}

		[HttpGet]
		public async Task<IActionResult> Index([FromQuery] int? profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			int activeProfileId = profileId ?? await GetActiveProfileIdSafeAsync(_db, userId);

			ViewBag.Profiles = await _db.Profile
				.Where(p => p.UserId == userId)
				.ToListAsync();

			ViewBag.ActiveProfileId = activeProfileId;
			return View();
		}

		// 1. Получить список диалогов СТРОГО для активного профиля
		[HttpGet("dialogs")]
		public async Task<IActionResult> GetDialogs([FromQuery] int profileId, [FromQuery] string? network = null)
		{
			var query = _db.InboxConversations
				.AsNoTracking()
				.Where(c => c.ProfileId == profileId);

			if (!string.IsNullOrEmpty(network) && network != "ALL")
			{
				int netId = network switch
				{
					"INSTAGRAM" => 1,
					"FACEBOOK" => 2,
					"TELEGRAM" => 3,
					"THREADS" => 4,
					"BLUESKY" => 5,
					_ => 0
				};
				if (netId > 0) query = query.Where(c => c.NetworkType == netId);
			}

			var dialogs = await query
				.OrderByDescending(c => c.LastMessageAt)
				.Take(50)
				.Select(c => new
				{
					id = c.Id,
					name = c.FullName ?? c.Username ?? "Клиент",
					username = c.Username,
					avatar = c.AvatarUrl,
					networkType = c.NetworkType,
					snippet = c.LastMessageSnippet,
					time = c.LastMessageAt.ToString("HH:mm"),
					unreadCount = c.UnreadCount,
					isTakeover = c.IsHumanTakeover
				})
				.ToListAsync();

			return Json(dialogs);
		}

		// 2. Получить историю сообщений выбранного чата
		[HttpGet("messages/{conversationId}")]
		public async Task<IActionResult> GetMessages(int conversationId)
		{
			var conv = await _db.InboxConversations.FirstOrDefaultAsync(c => c.Id == conversationId);
			if (conv == null) return NotFound();

			// Сбрасываем счетчик непрочитанных менеджером
			if (conv.UnreadCount > 0)
			{
				conv.UnreadCount = 0;
				await _db.SaveChangesAsync();
			}

			var messages = await _db.InboxMessages
				.AsNoTracking()
				.Where(m => m.ConversationId == conversationId)
				.OrderBy(m => m.CreatedAtUtc)
				.Take(50)
				.Select(m => new
				{
					id = m.Id,
					text = m.Text,
					isIncoming = m.IsIncoming,
					isSentByBot = m.IsSentByBot,
					mediaUrl = m.MediaUrl,
					time = m.CreatedAtUtc.ToString("HH:mm")
				})
				.ToListAsync();

			return Json(new
			{
				conversationId = conv.Id,
				name = conv.FullName ?? conv.Username ?? "Клиент",
				username = conv.Username,
				avatar = conv.AvatarUrl,
				networkType = conv.NetworkType,
				isTakeover = conv.IsHumanTakeover,
				messages = messages
			});
		}

		// 3. Отправка ответа менеджером руками
		[HttpPost("send")]
		public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Text)) return BadRequest("Текст пуст");

			var result = await _inboxService.SendManagerReplyAsync(dto.ConversationId, dto.Text.Trim());
			if (!result.Success) return BadRequest(result.ErrorMessage);

			return Ok(new { success = true });
		}

		// 4. Переключение тумблера Human Takeover
		[HttpPost("takeover")]
		public async Task<IActionResult> ToggleTakeover([FromBody] ToggleTakeoverDto dto)
		{
			bool success = await _inboxService.ToggleHumanTakeoverAsync(dto.ConversationId, dto.IsTakeover);
			return Ok(new { success });
		}

		// 5. Запуск первичной синхронизации существующих диалогов
		[HttpPost("sync")]
		public async Task<IActionResult> Sync([FromQuery] int profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// Проверяем, что профиль принадлежит текущему пользователю
			bool isMyProfile = await _db.Profile.AnyAsync(p => p.Id == profileId && p.UserId == userId);
			if (!isMyProfile) return Unauthorized();

			int syncedCount = await _inboxService.SyncProfileConversationsAsync(profileId);
			return Ok(new { success = true, count = syncedCount });
		}
	}

	public class SendMessageDto
	{
		public int ConversationId { get; set; }
		public string Text { get; set; } = string.Empty;
	}

	public class ToggleTakeoverDto
	{
		public int ConversationId { get; set; }
		public bool IsTakeover { get; set; }
	}
}