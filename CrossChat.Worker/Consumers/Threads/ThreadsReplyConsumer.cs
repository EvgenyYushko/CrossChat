using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.Threads;
public class ThreadsReplyConsumer : IConsumer<ThreadsEventReceived>
{
	private readonly ILogger<ThreadsReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IThreadsService _threadsService;
	private readonly IAiService _aiService;

	public ThreadsReplyConsumer(ILogger<ThreadsReplyConsumer> logger, AppDbContext db, IThreadsService threadsService, IAiService aiService)
	{
		_logger = logger; _db = db; _threadsService = threadsService; _aiService = aiService;
	}

	public async Task Consume(ConsumeContext<ThreadsEventReceived> context)
	{
		var msg = context.Message;

		// 1. Ищем настройки бота в БД
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.ThreadsUserId == msg.BotThreadsId);

		if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken))
			return;

		try
		{
			_logger.LogInformation($"[Threads Worker] Обработка {msg.Type} для {msg.BotThreadsId}");

			// 2. Формируем промпт
			var prompt = $"{settings.SystemPrompt}\n\nЭто сообщение из Threads ({msg.Type}). Пользователь @{msg.Username} пишет: {msg.Text}";

			// 3. Запрос к ИИ (пока без истории, для Threads это сложнее)
			//var aiResponse = await _aiService.GetAnswerAsync(prompt, new List<AiRequest>(), null);
			var aiResponse = "Hi))";

			if (string.IsNullOrWhiteSpace(aiResponse)) return;

			// 4. Отправка ответа в Threads
			// В Threads ответ — это создание поста с параметром reply_to_id
			await _threadsService.ReplyToThreadAsync(msg.MediaId, aiResponse, settings.AccessToken);

			_logger.LogInformation($"[Threads Worker] ✅ Ответ отправлен в ответ на {msg.MediaId}");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error responding to Threads event");
		}
	}
}