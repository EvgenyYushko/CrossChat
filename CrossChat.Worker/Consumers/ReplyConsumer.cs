using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers;

public class ReplyConsumer : IConsumer<ProcessDialogReply>
{
	private readonly ILogger<ReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IInstagramService _instaService;
	private readonly IAiService _aiService;

	// Статический лимитер (один на всё приложение)
	// 2. СОЗДАЕМ ЛИМИТЕР (Static - один на все потоки приложения)
	// Настройка: 15 запросов в 1 минуту (Безопасно для Free Tier)
	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 20,                     // Сколько разрешаем (20 шт)
		Window = TimeSpan.FromMinutes(1),     // За какое время (1 мин)
		QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
		QueueLimit = 0
	});

	// Сюда потом внедришь свои сервисы: IInstagramService, IAiService
	public ReplyConsumer(ILogger<ReplyConsumer> logger, AppDbContext db, IInstagramService instaService,
		IAiService aiService)
	{
		_logger = logger;
		_db = db;
		_instaService = instaService;
		_aiService = aiService;
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
		_logger.LogInformation($"Consume Start");

		// Пытаемся получить разрешение на выполнение
		using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, context.CancellationToken);

		if (!lease.IsAcquired)
		{
			// Лимит исчерпан -> бросаем исключение, чтобы сработал Redelivery (повтор через минуту)
			throw new Exception("Rate limit exceeded (Gemini). Triggering Redelivery.");
		}

		var senderId = context.Message.SenderId;       // Клиент (кто написал)
		var businessAccountId = context.Message.RecipientId; // Бот (кому написали)

		_logger.LogInformation($"[Reply] 🚀 Обработка диалога. BusinessID: {businessAccountId}, SenderID: {senderId}");

		// 2. Ищем настройки владельца бота в БД
		// Нам нужно найти того юзера, у которого InstagramBusinessId совпадает с RecipientId
		var settings = await _db.InstagramSettings
			.AsNoTracking() // Читаем без отслеживания для скорости
			.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);

		if (settings == null)
		{
			_logger.LogWarning($"[Reply] ❌ Настройки для BusinessID {businessAccountId} не найдены в БД. Игнорируем.");
			return;
		}

		if (!settings.IsActive)
		{
			_logger.LogInformation($"[Reply] ⏸ Бот выключен пользователем. Пропускаем.");
			return;
		}

		if (string.IsNullOrEmpty(settings.AccessToken))
		{
			_logger.LogError($"[Reply] ❌ Токен отсутствует для BusinessID {businessAccountId}.");
			return;
		}

		try
		{
			// 3. Получаем историю переписки (используя токен юзера)
			// Реализуешь получение истории в InstagramService позже
			var messages = await _instaService.GetHistoryAsync(senderId, settings.AccessToken);

			// 4. Отправляем в ИИ (через твой gRPC сервис)
			// Берем системный промпт из настроек
			var systemPrompt = settings.SystemPrompt ?? "Ты полезный помощник.";

			if (messages == null || messages.Count == 0) return;

			var lastMsg = messages[0];
			string lastSenderId = lastMsg.From.Id;
			if (lastSenderId == businessAccountId) return;

			int unreadCount = 0;
			foreach (var msg in messages) { if (msg.From.Id != businessAccountId) unreadCount++; else break; }

			var chatHistory = new List<AiRequest>();
			var unreadUserMessageIds = new List<string>(); // Для реакций

			// Идем по списку с конца (от старых) к началу (к новым), чтобы сохранить хронологию
			for (int i = messages.Count - 1; i >= 0; i--)
			{
				var msg = messages[i];

				// 1. Получаем текстовое содержание (с учетом кэша, фото, видео)
				string content = await ResolveMessageContentAsync(msg);

				// 2. Определяем роль для AI (model - это бот, user - это пользователь)
				string role = (msg.From.Id == businessAccountId) ? "model" : "user";

				// 3. Добавляем в историю в формате объектов
				chatHistory.Add(new AiRequest
				{
					Role = role,
					Text = string.IsNullOrEmpty(content) ? "[Empty message]" : content
				});

				// 4. Логика для Реакций:
				// Проверяем, является ли сообщение непрочитанным (по индексу unreadCount) и от пользователя
				// (unreadCount вычисляется перед этим циклом, как в прошлом коде)
				bool isUnread = i < unreadCount;
				if (isUnread && role == "user")
				{
					unreadUserMessageIds.Add(msg.Id);
				}
			}

			var aiResponse = await _aiService.GetAnswerAsync(systemPrompt, chatHistory, null);

			if (string.IsNullOrWhiteSpace(aiResponse))
			{
				_logger.LogWarning("[Reply] ИИ вернул пустой ответ.");
				return;
			}

			// 5. Отправляем ответ в Инстаграм
			await _instaService.SendMessageAsync(senderId, aiResponse, settings.AccessToken);

			_logger.LogInformation($"[Reply] ✅ Ответ успешно отправлен пользователю {senderId}");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"[Reply] 💥 Ошибка при обработке диалога {senderId}");
			// Здесь можно решить: бросать исключение (чтобы повторить попытку) или нет.
			// Если ошибка в логике (например, ИИ упал) - лучше повторить.
			throw;
		}
	}

	private async Task<string> ResolveMessageContentAsync(MessageItem msg)
	{
		if (!string.IsNullOrEmpty(msg.Text))
		{
			return msg.Text;
		}

		return "[Empty message]";
	}
}