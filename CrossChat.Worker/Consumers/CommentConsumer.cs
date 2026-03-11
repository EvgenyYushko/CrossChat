using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers;

public class CommentConsumer : IConsumer<InstagramCommentReceived>
{
	private readonly ILogger<CommentConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IInstagramService _instaService;
	private readonly IAiService _aiService;

	// Свой лимитер для комментов (чтобы не спамить)
	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 10,
		Window = TimeSpan.FromMinutes(1),
		QueueLimit = 0
	});

	public CommentConsumer(ILogger<CommentConsumer> logger, AppDbContext db, IInstagramService instaService, IAiService aiService)
	{
		_logger = logger; 
		_db = db;
		_instaService = instaService; 
		_aiService = aiService;
	}

	public async Task Consume(ConsumeContext<InstagramCommentReceived> context)
	{
		using var lease = await _rateLimiter.AcquireAsync(1, context.CancellationToken);
		if (!lease.IsAcquired) throw new Exception("Rate limit exceeded (Comments).");

		var msg = context.Message;

		// 1. Ищем настройки пользователя
		var settings = await _db.InstagramSettings
			.AsNoTracking()
			.FirstOrDefaultAsync(s => s.InstagramBusinessId == msg.BusinessAccountId);

		if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken))
		{
			_logger.LogInformation($"[Comment] Игнорируем коммент. Бот выключен или не настроен.");
			return;
		}

		try
		{
			// 2. Формируем промпт для ИИ
			// В системный промпт добавляем указание, что нужно ответить именно на комментарий
			var basePrompt = settings.SystemPrompt ?? "Ты вежливый помощник.";

			var fullPrompt = $@"{basePrompt}
ВАЖНО: Сейчас ты отвечаешь на ПУБЛИЧНЫЙ КОММЕНТАРИЙ под постом (а не в личных сообщениях).
Пользователь @{msg.Username} написал: ""{msg.Text}""
Ответь кратко, дружелюбно, в 1-2 предложения. Можешь упомянуть пользователя по нику.";

			// 3. Отправляем в ИИ
			// Для комментов история пустая, передаем только этот промпт
			var aiResponse = await _aiService.GeminiRequest(fullPrompt, null);

			if (string.IsNullOrWhiteSpace(aiResponse)) return;

			// 4. Отвечаем в Инстаграм
			await _instaService.ReplyToCommentAsync(msg.CommentId, aiResponse, settings.AccessToken);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"[Comment] Ошибка при ответе на коммент {msg.CommentId}");
			throw; // Бросаем, чтобы MassTransit повторил
		}
	}
}