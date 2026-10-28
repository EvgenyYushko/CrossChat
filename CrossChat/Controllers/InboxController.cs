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
		/// Бесплатный переводчик без блокировок дата-центра (MyMemory ➔ LibreTranslate ➔ Резерв Gemini)
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

			// Проверка на реальный перевод (чтобы не возвращать тот же текст)
			bool IsRealTranslation(string? result)
			{
				if (string.IsNullOrWhiteSpace(result)) return false;
				return !string.Equals(result.Trim(), text, StringComparison.OrdinalIgnoreCase);
			}

			// ===================================================================
			// ЛИНИЯ 1: MyMemory API (Официальный бесплатный REST API, 0 рублей, не банит Render)
			// ===================================================================
			try
			{
				// Параметр de=support@crosschat.ru поднимает бесплатный лимит до 50 000 символов/день
				string url1 = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=auto|ru&de=support@crosschat.ru";
				using var req1 = new HttpRequestMessage(HttpMethod.Get, url1);
				req1.Headers.UserAgent.ParseAdd("CrossChat-App/1.0");

				var resp1 = await _httpClient.SendAsync(req1);
				_logger.LogInformation("[Translate Line 1 - MyMemory] HTTP Статус: {Code}", resp1.StatusCode);

				if (resp1.IsSuccessStatusCode)
				{
					var json1 = await resp1.Content.ReadAsStringAsync();
					using var doc1 = JsonDocument.Parse(json1);
					var root1 = doc1.RootElement;

					if (root1.TryGetProperty("responseData", out var respData) &&
						respData.TryGetProperty("translatedText", out var trText))
					{
						string? trans = trText.GetString();
						if (!string.IsNullOrWhiteSpace(trans) && !trans.StartsWith("MYMEMORY WARNING"))
						{
							trans = System.Net.WebUtility.HtmlDecode(trans).Trim();
							if (IsRealTranslation(trans))
							{
								_logger.LogInformation("✅ [Translate] Успех через Линию 1 (MyMemory): «{Trans}»", trans);
								return Ok(new { success = true, translation = trans });
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning("[Translate Line 1 - MyMemory] Ошибка: {Msg}", ex.Message);
			}

			// ===================================================================
			// ЛИНИЯ 2: LibreTranslate (Открытый сервер нейро-перевода)
			// ===================================================================
			try
			{
				var payload2 = new
				{
					q = text,
					source = "auto",
					target = "ru",
					format = "text"
				};

				var jsonContent = new StringContent(JsonSerializer.Serialize(payload2), Encoding.UTF8, "application/json");
				var resp2 = await _httpClient.PostAsync("https://translate.terraprint.co/translate", jsonContent);
				_logger.LogInformation("[Translate Line 2 - LibreTranslate] HTTP Статус: {Code}", resp2.StatusCode);

				if (resp2.IsSuccessStatusCode)
				{
					var json2 = await resp2.Content.ReadAsStringAsync();
					using var doc2 = JsonDocument.Parse(json2);
					if (doc2.RootElement.TryGetProperty("translatedText", out var trText))
					{
						string? trans = trText.GetString();
						if (IsRealTranslation(trans))
						{
							_logger.LogInformation("✅ [Translate] Успех через Линию 2 (LibreTranslate): «{Trans}»", trans);
							return Ok(new { success = true, translation = trans!.Trim() });
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning("[Translate Line 2 - LibreTranslate] Ошибка: {Msg}", ex.Message);
			}

			// ===================================================================
			// ЛИНИЯ 3 (АВАРИЙНЫЙ РЕЗЕРВ): Gemini ТОЛЬКО если бесплатные упали
			// ===================================================================
			try
			{
				_logger.LogWarning("⚠️ [Translate] Бесплатные сервисы временно недоступны. Включаем аварийный резерв Gemini...");
				var prompt = $"Translate this social media message to Russian naturally. Keep emojis. Output ONLY the translation without quotes:\n\n{text}";
				var aiTrans = await _aiService.GeminiRequest(prompt, null);

				if (!string.IsNullOrWhiteSpace(aiTrans))
				{
					string cleanAi = aiTrans.Trim().Trim('"', '«', '»');
					_logger.LogInformation("✅ [Translate] Переведено через резервный Gemini: «{Trans}»", cleanAi);
					return Ok(new { success = true, translation = cleanAi });
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [Translate] Фатальный сбой всех линий перевода");
			}

			return BadRequest("Не удалось перевести сообщение");
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