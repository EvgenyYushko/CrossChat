using CrossChat.Data;
using CrossChat.Data.Entities;
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
	private readonly IThreadsConsole _console;
	private readonly IDatabase _redis;

	public ThreadsReplyConsumer(
		ILogger<ThreadsReplyConsumer> logger, 
		AppDbContext db,
		IThreadsService threadsService, 
		IAiService aiService, 
		IConnectionMultiplexer redis, 
		IThreadsConsole console)
	{
		_logger = logger; 
		_db = db; 
		_threadsService = threadsService;
		_aiService = aiService;
		_console = console;
		_redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ThreadsEventReceived> context)
	{
		var msg = context.Message;

		// 1. ЗАЩИТА ОТ ДУБЛЕЙ ВЕБХУКОВ
		var lockKey = $"processed_threads:{msg.MediaId}:{msg.BotThreadsId}";
		if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromMinutes(10), When.NotExists))
		{
			_logger.LogInformation($"[Threads] Сообщение {msg.MediaId} уже обрабатывается. Пропускаем дубль.");
			return;
		}

		// 2. ПРАВИЛО: НЕ БОЛЕЕ 1 ОТВЕТА ПОЛЬЗОВАТЕЛЮ ПОД ОДНИМ ПОСТОМ (24 часа)
		if (!string.IsNullOrEmpty(msg.RootPostId))
		{
			var userPostKey = $"threads_answered_user:{msg.RootPostId}:{msg.Username}";
			if (!await _redis.StringSetAsync(userPostKey, "answered", TimeSpan.FromHours(24), When.NotExists))
			{
				_logger.LogInformation($"[Threads] Мы уже отвечали пользователю @{msg.Username} под постом {msg.RootPostId}. Пропускаем.");
				return;
			}
		}

		// 3. Ищем бота в БД
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.ThreadsUserId == msg.BotThreadsId);
		if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken)) return;

		int replyMode = settings.ReplyMode > 0 ? settings.ReplyMode : 2;
		string? replyText = null;

		try
		{
			await _console.Log($"Обработка реплая от @{msg.Username} на '{msg.Text}' (Режим: {replyMode})", settings.UserId, settings.Id);

			// === ГЕНЕРАЦИЯ ТЕКСТА (Шаблоны / ИИ) ===
			if (replyMode == 2)
			{
				replyText = InstagramCommentEngine.GetRandomTemplate(settings.ReplyTemplates);
			}
			else if (replyMode == 1)
			{
				replyText = await GenerateAiThreadsReply(settings, msg);
			}
			else if (replyMode == 3)
			{
				try
				{
					replyText = await GenerateAiThreadsReply(settings, msg);
				}
				catch (Exception aiEx)
				{
					_logger.LogWarning(aiEx, "[Threads] Сбой ИИ для реплая {MediaId}. Переход на резервный шаблон.", msg.MediaId);
				}

				if (string.IsNullOrWhiteSpace(replyText))
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(settings.ReplyTemplates);
				}
			}

			if (string.IsNullOrWhiteSpace(replyText))
			{
				_logger.LogWarning("[Threads] Текст ответа пуст для {MediaId}", msg.MediaId);
				return;
			}

			// 4. Создаем контейнер в Threads
			var creationId = await _threadsService.CreateReplyContainerAsync(msg.MediaId, replyText, settings.AccessToken);

			// ==================================================================================
			// 5. УМНАЯ СКОЛЬЗЯЩАЯ ОЧЕРЕДЬ (PACING QUEUE): РАСПРЕДЕЛЯЕМ ОТВЕТЫ ВО ВРЕМЕНИ
			// ==================================================================================
			var queueTimeKey = $"threads:next_reply_time:{settings.Id}";
			var now = DateTime.UtcNow;

			// Узнаем, когда запланирован ответ для предыдущего человека
			var lastScheduledTicks = await _redis.StringGetAsync(queueTimeKey);
			DateTime baseTime = now;

			if (lastScheduledTicks.HasValue && long.TryParse(lastScheduledTicks, out long ticks))
			{
				var lastTime = new DateTime(ticks, DateTimeKind.Utc);
				// Если в очереди уже стоят другие ответы — пристраиваемся ЗА ними!
				if (lastTime > now)
				{
					baseTime = lastTime;
				}
			}

			// Добавляем случайную паузу от 20 до 40 секунд (имитация скорости печати человека)
			int humanPauseSeconds = Random.Shared.Next(20, 41);
			var targetTimeUtc = baseTime.AddSeconds(humanPauseSeconds);

			// Обновляем крайнюю временную метку очереди в Redis (храним 2 часа)
			await _redis.StringSetAsync(queueTimeKey, targetTimeUtc.Ticks.ToString(), TimeSpan.FromHours(2));

			// Считаем точную задержку для MassTransit/Quartz
			var finalDelay = targetTimeUtc - now;

			_logger.LogInformation($"[Threads] ⏱ Ответ для @{msg.Username} поставлен в очередь. Публикация через {finalDelay.TotalSeconds:F0} сек.");

			// Планируем публикацию с вычисленной персональной задержкой!
			await context.SchedulePublish(finalDelay, new PublishThreadsCommand
			{
				BotDbId = settings.Id,
				CreationId = creationId,
				TargetMediaId = msg.MediaId,
				Username = msg.Username
			});

			await _console.Log($"Ответ для @{msg.Username} запланирован (пауза {finalDelay.TotalSeconds:F0}с): «{replyText}»", settings.UserId, settings.Id);
		}
		catch (Exception ex)
		{
			await _redis.KeyDeleteAsync(lockKey);
			_logger.LogError(ex, "Ошибка при подготовке ответа Threads");
		}
	}

	private async Task<string?> GenerateAiThreadsReply(ThreadsSettings settings, ThreadsEventReceived msg)
	{
		var prompt = $"{settings.SystemPrompt}\n\nYou are replying to a comment in the Topics section. User @{msg.Username} wrote: '{msg.Text}'. Reply in a lively and concise manner, and strictly in the language they used.";
		return await _aiService.GeminiRequest(prompt, null);
	}
}