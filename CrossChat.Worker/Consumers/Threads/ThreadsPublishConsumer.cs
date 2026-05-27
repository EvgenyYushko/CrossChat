using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.Threads;

public class ThreadsPublishConsumer : IConsumer<PublishThreadsCommand>
{
	private readonly IThreadsService _threadsService;
	private readonly AppDbContext _db;
	private readonly ILogger<ThreadsPublishConsumer> _logger;
	private readonly IThreadsConsole _console;

	public ThreadsPublishConsumer(IThreadsService threadsService, AppDbContext db
		, ILogger<ThreadsPublishConsumer> logger, IThreadsConsole console)
	{
		_threadsService = threadsService; _db = db; _logger = logger;
		_console = console;
	}

	public async Task Consume(ConsumeContext<PublishThreadsCommand> context)
	{
		var msg = context.Message;
		var settings = await _db.ThreadsSettings.FirstOrDefaultAsync(s => s.Id == msg.BotDbId);
		if (settings == null) return;

		// 1. Проверяем статус (FINISHED / IN_PROGRESS)
		var status = await _threadsService.GetContainerStatusAsync(msg.CreationId, settings.AccessToken);

		if (status == "FINISHED")
		{
			// 2. Публикуем!
			await _threadsService.PublishReplyAsync(msg.CreationId, settings.AccessToken);
			await _console.Log($"✅ Ответ на {msg.TargetMediaId} опубликован!", settings.UserId, settings.Id);
		}
		else if (status == "IN_PROGRESS")
		{
			// Если еще не готов — откладываем еще на 20 секунд
			_logger.LogInformation($"[Threads] ⏳ Контейнер {msg.CreationId} еще готовится. Snooze...");
			await context.SchedulePublish(TimeSpan.FromSeconds(20), msg);
		}
		else if (status == "PUBLISHED")
		{
			_logger.LogInformation($"[Threads] ⏳ Контейнер {msg.CreationId} уже опубликован...");
		}
		else
		{
			_logger.LogError($"[Threads] ❌ Ошибка публикации {msg.CreationId}: статус {status}");
		}
	}
}
