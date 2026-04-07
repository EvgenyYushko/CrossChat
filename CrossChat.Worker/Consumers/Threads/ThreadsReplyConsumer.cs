using System.Threading.RateLimiting;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.Threads;

public class ThreadsReplyConsumer : IConsumer<ThreadsProcessReply>
{
	private readonly ILogger<ThreadsReplyConsumer> _logger;
	private readonly AppDbContext _db;
	private readonly IThreadsService _threadsService;
	private readonly IAiService _aiService;
	private readonly IDatabase _redis;

	// ЛИМИТЕР: 1 ответ в 30 секунд (как ты просил)
	private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
	{
		PermitLimit = 1,
		Window = TimeSpan.FromSeconds(30),
		QueueLimit = 100 // Очередь ожидания внутри памяти воркера
	});

	public ThreadsReplyConsumer(ILogger<ThreadsReplyConsumer> logger, AppDbContext db,
		IThreadsService threadsService, IAiService aiService, IConnectionMultiplexer redis)
	{
		_logger = logger; _db = db;
		_threadsService = threadsService;
		_aiService = aiService;
		_redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ThreadsProcessReply> context)
	{
		// 1. Ждем своей очереди (раз в 30 секунд)
		using var lease = await _rateLimiter.AcquireAsync(1, context.CancellationToken);
		if (!lease.IsAcquired) throw new Exception("Rate limit exceeded");

		var msg = context.Message;

		// 2. Достаем свежие настройки бота из БД
		var settings = await _db.ThreadsSettings.FindAsync(msg.BotId);
		if (settings == null || !settings.IsActive) return;

		try
		{
			// 3. Генерируем ответ
			var prompt = $"{settings.SystemPrompt}\n\nПользователь @{msg.Username} написал комментарий: {msg.UserText}. Ответь ему.";
			//var aiResponse = await _aiService.GetAnswerAsync(prompt, new List<AiRequest>(), null);
			var aiResponse = "❤️";

			if (string.IsNullOrWhiteSpace(aiResponse)) return;

			// 4. Публикуем в Threads
			var creationId = await _threadsService.CreateReplyContainerAsync(msg.TargetMediaId, aiResponse, settings.AccessToken);
			var isReady = await _threadsService.WaitForMediaReadyAsync(creationId, settings.AccessToken);
			if (!isReady)
			{
				throw new Exception($"Медиа {creationId} не готово к публикации после ожидания");
			}

			await _threadsService.PublishReplyAsync(creationId, settings.AccessToken);

			_logger.LogInformation($"[ThreadsWorker] Отправлен ответ для @{msg.Username} на коммент {msg.TargetMediaId}");
		}
		catch (Exception ex)
		{
			// Если ошибка - удаляем метку из Redis, чтобы следующая джоба могла снова найти этот коммент
			await _redis.KeyDeleteAsync($"threads_queued:{msg.TargetMediaId}");
			_logger.LogError(ex, "Ошибка при ответе в Threads");
			throw; // Делаем ретрай через MassTransit
		}
	}
}