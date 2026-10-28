using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CrossChat.Data;
using CrossChat.Integrations.Enums;
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
		private readonly HttpClient _httpClient; // <-- ОБЪЯВЛЯЕМ ПОЛЕ
		private readonly IAiService _aiService;

		public InboxController(
			AppDbContext db,
			IInboxService inboxService,
			ILogger<InboxController> logger,
			HttpClient httpClient,
			IAiService aiService) // <-- ВНЕДРЯЕМ В КОНСТРУКТОР
		{
			_db = db;
			_inboxService = inboxService;
			_logger = logger;
			_httpClient = httpClient;
			_aiService = aiService;
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
				if (network == "TELEGRAM")
				{
					// Telegram у нас разбит на 3 типа: Public (4), Private (5), Channel (6)
					query = query.Where(c => c.NetworkType == (int)NetworkType.TelegramPublic ||
											 c.NetworkType == (int)NetworkType.TelegramPrivate ||
											 c.NetworkType == (int)NetworkType.TelegramChannel);
				}
				else
				{
					int netId = network switch
					{
						"INSTAGRAM" => (int)NetworkType.Instagram, // 1
						"FACEBOOK" => (int)NetworkType.Facebook,   // 2
						"BLUESKY" => (int)NetworkType.BlueSky,     // 3
						"THREADS" => (int)NetworkType.Threads,     // 8
						"X" => (int)NetworkType.X,                 // 7
						_ => 0
					};
					if (netId > 0) query = query.Where(c => c.NetworkType == netId);
				}
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

		// 2. Получить историю сообщений выбранного чата (СВЕЖИЕ 50 СООБЩЕНИЙ)
		[HttpGet("messages/{conversationId}")]
		public async Task<IActionResult> GetMessages(int conversationId)
		{
			var conv = await _db.InboxConversations.FirstOrDefaultAsync(c => c.Id == conversationId);
			if (conv == null) return NotFound();

			if (conv.UnreadCount > 0)
			{
				conv.UnreadCount = 0;
				await _db.SaveChangesAsync();
			}

			// 1. Берем 50 САМЫХ ПОСЛЕДНИХ сообщений из базы:
			var rawMessages = await _db.InboxMessages
				.AsNoTracking()
				.Where(m => m.ConversationId == conversationId)
				.OrderByDescending(m => m.CreatedAtUtc) // Сначала самые новые!
				.Take(100)
				.ToListAsync();

			// 2. Разворачиваем хронологически (старые сверху, новые снизу)
			var messages = rawMessages
				.OrderBy(m => m.CreatedAtUtc)
				.Select(m => new
				{
					id = m.Id,
					text = m.Text,
					isIncoming = m.IsIncoming,
					isSentByBot = m.IsSentByBot,
					mediaUrl = m.MediaUrl,
					mediaType = m.MediaType, // <-- ДОБАВЛЕНО
					reaction = m.Reaction, // Подтягиваем сохраненное сердечко!
					time = m.CreatedAtUtc.ToString("HH:mm")
				})
				.ToList();

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

		// 6. Тоггл реакции (поставить / снять лайк)
		[HttpPost("toggle-reaction")]
		public async Task<IActionResult> ToggleReaction([FromBody] ToggleReactionDto dto)
		{
			var (success, newReaction) = await _inboxService.ToggleMessageReactionAsync(dto.MessageId);
			return Ok(new { success, reaction = newReaction });
		}

		// ==========================================================
		// ПОТОКОВЫЙ ПРОКСИ ДЛЯ МЕДИАФАЙЛОВ (ОБХОД ОШИБОК 403 И МОБИЛЬНЫХ ОГРАНИЧЕНИЙ)
		// Файлы НЕ сохраняются на диск, а транслируются напрямую в браузер
		// ==========================================================
		// ==========================================================
		// ПОТОКОВЫЙ ПРОКСИ ДЛЯ МЕДИАФАЙЛОВ (ИСПРАВЛЕНО: БЕЗ ОБРЫВА СОКЕТОВ И С ЗАЩИТОЙ ОТ БЛОКИРОВОК META)
		// ==========================================================
		[HttpGet("media/{messageId}")]
		public async Task<IActionResult> GetMediaStream(int messageId)
		{
			var msg = await _db.InboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);
			if (msg == null || string.IsNullOrEmpty(msg.MediaUrl))
				return NotFound();

			try
			{
				// 1. Формируем запрос с браузерным User-Agent (Meta не будет сбрасывать сокет)
				using var request = new HttpRequestMessage(HttpMethod.Get, msg.MediaUrl);
				request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

				// 2. Используем внедренный _httpClient контроллера (он не уничтожается раньше времени!)
				var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
				if (!response.IsSuccessStatusCode)
				{
					_logger.LogWarning("[Inbox Media] Meta CDN вернула статус {Code} для сообщения {MsgId}", response.StatusCode, messageId);
					return NotFound();
				}

				var contentType = response.Content.Headers.ContentType?.MediaType
								  ?? (msg.MediaType == "video" ? "video/mp4" : "image/jpeg");

				// 3. Считываем байты в оперативную память (для фото это 200 КБ, для видео 1-2 МБ)
				// Это полностью исключает SocketException (104) Connection reset by peer!
				var fileBytes = await response.Content.ReadAsByteArrayAsync();

				// 4. Разрешаем браузеру кэшировать файл на 1 час (для мгновенного скролла)
				Response.Headers.CacheControl = "public, max-age=3600";

				return File(fileBytes, contentType);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "[Inbox Media] Ошибка проксирования файла для сообщения {MsgId}", messageId);
				return NotFound();
			}
		}

		// 7. Удаление сообщения из Инбокса менеджером
		[HttpPost("delete-message")]
		public async Task<IActionResult> DeleteMessage([FromBody] DeleteMessageDto dto)
		{
			bool success = await _inboxService.DeleteMessageFromInboxAsync(dto.MessageId);
			return Ok(new { success });
		}

		// 8. Отправка медиафайла (скрепочка 📎) менеджером
		[HttpPost("send-media")]
		[RequestSizeLimit(50 * 1024 * 1024)] // Лимит 50 МБ
		public async Task<IActionResult> SendMedia([FromForm] int conversationId, [FromForm] IFormFile file)
		{
			if (file == null || file.Length == 0)
				return BadRequest("Файл не выбран");

			var (success, error) = await _inboxService.SendManagerMediaReplyAsync(conversationId, file);
			if (!success)
				return BadRequest(error);

			return Ok(new { success = true });
		}

		/// <summary>
		/// Бесплатный каскадный переводчик с подробным логированием каждого шага
		/// </summary>
		[HttpPost("translate")]
		[IgnoreAntiforgeryToken]
		public async Task<IActionResult> TranslateMessage([FromBody] TranslateRequestDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto?.Text))
				return BadRequest("Текст пуст");

			string text = dto.Text.Trim();
			_logger.LogInformation("==================================================");
			_logger.LogInformation("[Translate] 🌐 Новый запрос перевода: «{Text}»", text);

			// ===================================================================
			// ЛИНИЯ 1: Google Translate (Классический GTX с браузерными заголовками)
			// ===================================================================
			try
			{
				string url1 = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ru&dt=t&q={Uri.EscapeDataString(text)}";
				using var req1 = new HttpRequestMessage(HttpMethod.Get, url1);
				req1.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
				req1.Headers.Add("Accept", "*/*");
				req1.Headers.Add("Referer", "https://translate.google.com/");

				var resp1 = await _httpClient.SendAsync(req1);
				var body1 = await resp1.Content.ReadAsStringAsync();
				_logger.LogInformation("[Translate Line 1 - Google GTX] Статус: {Code}, Длина ответа: {Len}", resp1.StatusCode, body1.Length);

				if (resp1.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(body1);
					var root = doc.RootElement;
					if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
					{
						var sentences = root[0];
						var sb = new StringBuilder();
						foreach (var s in sentences.EnumerateArray())
						{
							if (s.GetArrayLength() > 0 && s[0].ValueKind == JsonValueKind.String)
							{
								sb.Append(s[0].GetString());
							}
						}

						if (sb.Length > 0)
						{
							_logger.LogInformation("✅ [Translate] Успех через Линию 1 (Google GTX): «{Trans}»", sb.ToString());
							return Ok(new { success = true, translation = sb.ToString().Trim() });
						}
					}
				}
				else
				{
					_logger.LogWarning("[Translate Line 1 - Google GTX] Отказ: {BodyPreview}", body1.Length > 150 ? body1.Substring(0, 150) : body1);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning("[Translate Line 1 - Google GTX] Исключение: {Msg}", ex.Message);
			}

			// ===================================================================
			// ЛИНИЯ 2: Lingva Translate (Специальный открытый шлюз Google Translate)
			// ===================================================================
			try
			{
				string url2 = $"https://lingva.ml/api/v1/auto/ru/{Uri.EscapeDataString(text)}";
				using var req2 = new HttpRequestMessage(HttpMethod.Get, url2);
				req2.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

				var resp2 = await _httpClient.SendAsync(req2);
				var body2 = await resp2.Content.ReadAsStringAsync();
				_logger.LogInformation("[Translate Line 2 - Lingva] Статус: {Code}, Ответ: {Body}", resp2.StatusCode, body2.Length > 100 ? body2.Substring(0, 100) : body2);

				if (resp2.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(body2);
					if (doc.RootElement.TryGetProperty("translation", out var trProp))
					{
						string? trans = trProp.GetString();
						if (!string.IsNullOrWhiteSpace(trans))
						{
							_logger.LogInformation("✅ [Translate] Успех через Линию 2 (Lingva Google): «{Trans}»", trans);
							return Ok(new { success = true, translation = trans.Trim() });
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning("[Translate Line 2 - Lingva] Исключение: {Msg}", ex.Message);
			}

			// ===================================================================
			// ЛИНИЯ 3: Google Chrome Extension API (clients5.google.com)
			// ===================================================================
			try
			{
				string url3 = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=ru&q={Uri.EscapeDataString(text)}";
				using var req3 = new HttpRequestMessage(HttpMethod.Get, url3);
				req3.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

				var resp3 = await _httpClient.SendAsync(req3);
				var body3 = await resp3.Content.ReadAsStringAsync();
				_logger.LogInformation("[Translate Line 3 - Google Chrome API] Статус: {Code}", resp3.StatusCode);

				if (resp3.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(body3);
					var root = doc.RootElement;
					if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
					{
						var first = root[0];
						string? trans = first.ValueKind == JsonValueKind.String
							? first.GetString()
							: (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 ? first[0].GetString() : null);

						if (!string.IsNullOrWhiteSpace(trans))
						{
							_logger.LogInformation("✅ [Translate] Успех через Линию 3 (Google Chrome): «{Trans}»", trans);
							return Ok(new { success = true, translation = trans.Trim() });
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning("[Translate Line 3 - Google Chrome API] Исключение: {Msg}", ex.Message);
			}

			// ===================================================================
			// ЛИНИЯ 4 (ЖЕЛЕЗНЫЙ РЕЗЕРВ): Gemini только если все бесплатные упали
			// ===================================================================
			try
			{
				_logger.LogWarning("⚠️ [Translate] Все 3 бесплатные линии не смогли перевести. Включаем аварийный резерв Gemini...");
				var prompt = $"Translate the following message to Russian naturally. Return ONLY the translated text, no quotes:\n\n{text}";
				var aiTrans = await _aiService.GeminiRequest(prompt, null);

				if (!string.IsNullOrWhiteSpace(aiTrans))
				{
					_logger.LogInformation("✅ [Translate] Переведено через аварийный Gemini: «{Trans}»", aiTrans);
					return Ok(new { success = true, translation = aiTrans.Trim().Trim('"', '«', '»') });
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [Translate] Фатальный сбой всех линий перевода");
			}

			return BadRequest("Не удалось перевести сообщение ни одним сервисом");
		}

		public class TranslateRequestDto
		{
			public string Text { get; set; } = string.Empty;
		}
	}

	public class DeleteMessageDto
	{
		public int MessageId { get; set; }
	}

	public class ToggleReactionDto
	{
		public int MessageId { get; set; }
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