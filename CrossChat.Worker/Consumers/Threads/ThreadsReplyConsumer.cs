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

	public ThreadsReplyConsumer(ILogger<ThreadsReplyConsumer> logger, AppDbContext db,
		IThreadsService threadsService, IAiService aiService, IConnectionMultiplexer redis, IThreadsConsole console)
	{
		_logger = logger; _db = db; _threadsService = threadsService;
		_aiService = aiService;
		_console = console;
		_redis = redis.GetDatabase();
	}

	public async Task Consume(ConsumeContext<ThreadsEventReceived> context)
	{
		var msg = context.Message;

		// 1. ЗАЩИТА ОТ ДУБЛЕЙ
		var lockKey = $"processed_threads:{msg.MediaId}:{msg.BotThreadsId}";
		if (!await _redis.StringSetAsync(lockKey, "processing", TimeSpan.FromMinutes(5), When.NotExists))
		{
			_logger.LogInformation($"[Threads] Сообщение {msg.MediaId} уже в обработке. Игнорируем дубль.");
			return;
		}

		// 2. Ищем бота в БД по ThreadsUserId
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.ThreadsUserId == msg.BotThreadsId);
		if (settings == null || !settings.IsActive || string.IsNullOrEmpty(settings.AccessToken)) return;

		int replyMode = settings.ReplyMode > 0 ? settings.ReplyMode : 2;
		string? replyText = null;

		try
		{
			await _console.Log($"Обработка реплая от @{msg.Username} на '{msg.Text}' (Режим: {replyMode})", settings.UserId, settings.Id);

			// === СЦЕНАРИЙ 2: ТОЛЬКО ШАБЛОНЫ (Spintax + Очеловечивание) ===
			if (replyMode == 2)
			{
				replyText = InstagramCommentEngine.GetRandomTemplate(settings.ReplyTemplates);
			}
			// === СЦЕНАРИЙ 1: ТОЛЬКО ИИ ===
			else if (replyMode == 1)
			{
				replyText = await GenerateAiThreadsReply(settings, msg);
			}
			// === СЦЕНАРИЙ 3: КОМБИНИРОВАННЫЙ (ИИ с фоллбеком на Spintax-шаблоны) ===
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
					_logger.LogInformation("[Threads] Использован резервный шаблон для {MediaId}", msg.MediaId);
					replyText = InstagramCommentEngine.GetRandomTemplate(settings.ReplyTemplates);
				}
			}

			if (string.IsNullOrWhiteSpace(replyText))
			{
				_logger.LogWarning("[Threads] Текст ответа пуст (шаблоны не заполнены) для {MediaId}", msg.MediaId);
				return;
			}

			// 4. Создаем контейнер ответа в Meta
			var creationId = await _threadsService.CreateReplyContainerAsync(msg.MediaId, replyText, settings.AccessToken);

			_logger.LogInformation($"[Threads] Контейнер {creationId} создан. Ждем 30с до публикации.");

			// 5. Планируем публикацию через 30 секунд в Quartz
			await context.SchedulePublish(TimeSpan.FromSeconds(30), new PublishThreadsCommand
			{
				BotDbId = settings.Id,
				CreationId = creationId,
				TargetMediaId = msg.MediaId,
				Username = msg.Username
			});

			await _console.Log($"Подготовлен ответ для @{msg.Username}: «{replyText}»", settings.UserId, settings.Id);
		}
		catch (Exception ex)
		{
			await _redis.KeyDeleteAsync(lockKey);
			_logger.LogError(ex, "Ошибка при подготовке ответа Threads");
		}
	}

	private async Task<string?> GenerateAiThreadsReply(ThreadsSettings settings, ThreadsEventReceived msg)
	{
		var prompt = $"{settings.SystemPrompt}\n\nТы отвечаешь на комментарий (реплай) в Threads. Пользователь @{msg.Username} написал: '{msg.Text}'. Ответь живо и кратко.";
		return await _aiService.GeminiRequest(prompt, null);
	}
}