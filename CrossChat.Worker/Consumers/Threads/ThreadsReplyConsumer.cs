using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Threads;

public class ThreadsReplyConsumer : IConsumer<ThreadsEventReceived>
{
	private readonly ILogger<ThreadsReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IThreadsService _threadsService;
	private readonly IAiService _aiService;
	private readonly IDatabase _redis;

	public ThreadsReplyConsumer(ILogger<ThreadsReplyConsumer> logger, AppDbContext db,
		IThreadsService threadsService, IAiService aiService, IConnectionMultiplexer redis)
	{
		_logger = logger; _db = db; _threadsService = threadsService;
		_aiService = aiService; _redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ThreadsEventReceived> context)
	{
		var msg = context.Message;

		// 1. ЗАЩИТА ОТ ДУБЛЕЙ (так как Meta прислала 2 вебхука в одну секунду)
		var lockKey = $"processed_threads:{msg.MediaId}:{msg.BotThreadsId}";
		if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromMinutes(5), When.NotExists))
		{
			_logger.LogInformation($"[Threads] Сообщение {msg.MediaId} уже в обработке. Игнорируем дубль.");
			return;
		}

		// 2. Ищем бота в БД по ThreadsUserId (target_id из вебхука)
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.ThreadsUserId == msg.BotThreadsId);
		if (settings == null || !settings.IsActive) return;

		try
		{
			_logger.LogInformation($"[Threads] Генерируем ответ для @{msg.Username} на '{msg.Text}'");

			// 3. Запрос к ИИ
			var prompt = $"{settings.SystemPrompt}\n\nТы отвечаешь в Threads. Пользователь @{msg.Username} написал: {msg.Text}";
			//var aiResponse = await _aiService.GetAnswerAsync(prompt, new List<AiRequest>(), null);
			var aiResponse = "❤️";

			if (string.IsNullOrWhiteSpace(aiResponse)) return;

			// 4. Создаем контейнер ответа в Meta
			var creationId = await _threadsService.CreateReplyContainerAsync(msg.MediaId, aiResponse, settings.AccessToken);

			_logger.LogInformation($"[Threads] Контейнер {creationId} создан. Ждем 30с до публикации.");

			// 5. ПЛАНИРУЕМ ПУБЛИКАЦИЮ ЧЕРЕЗ 30 СЕКУНД
			// Мы не спим в потоке, а отдаем задачу в Quartz
			await context.SchedulePublish(TimeSpan.FromSeconds(30), new PublishThreadsCommand
			{
				BotDbId = settings.Id, // используем UserId как ключ
				CreationId = creationId,
				TargetMediaId = msg.MediaId
			});
		}
		catch (Exception ex)
		{
			await _redis.KeyDeleteAsync(lockKey); // Снимаем замок при ошибке
			_logger.LogError(ex, "Ошибка при подготовке ответа Threads");
		}
	}
}