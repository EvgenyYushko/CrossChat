using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs;

[DisallowConcurrentExecution]
public class ThreadsAutoReplyJob : IJob
{
	private readonly AppDbContext _db;
	private readonly IThreadsService _threadsService;
	private readonly IAiService _aiService;
	private readonly ILogger<ThreadsAutoReplyJob> _logger;
	private readonly IDatabase _redis;
	private readonly IPublishEndpoint _publishEndpoint;
	private readonly IThreadsConsole _console;

	public ThreadsAutoReplyJob(AppDbContext db, IThreadsService threadsService, IAiService aiService
		, ILogger<ThreadsAutoReplyJob> logger
		, IConnectionMultiplexer redis
		, IPublishEndpoint publishEndpoint
		, IThreadsConsole console)
	{
		_db = db;
		_threadsService = threadsService;
		_aiService = aiService;
		_logger = logger;
		_redis = redis.GetDatabase();
		_publishEndpoint = publishEndpoint;
		_console = console;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		// 1. Берем ОДНОГО бота, которого дольше всего не проверяли
		var bot = await _db.ThreadsSettings
			.Where(s => s.IsActive && s.AccessToken != null)
			.OrderBy(s => s.LastProcessedAt)
			.FirstOrDefaultAsync();

		if (bot == null) return;

		_logger.LogInformation($"[ThreadsJob] Проверка аккаунта @{bot.Username}");

		try
		{
			// 2. Получаем список всех постов (threads) бота
			var myThreads = await _threadsService.GetUserThreadsAsync(bot.AccessToken);

			foreach (var thread in myThreads)
			{
				await _console.Log($"Проверка аккаунта @{bot.Username}", int.Parse(thread.Id), int.Parse(thread.Id));

				// 3. Получаем дерево комментариев для этого поста
				var conversation = await _threadsService.GetConversationAsync(thread.Id, bot.AccessToken);

				// 4. Ищем комментарии, на которые МЫ (бот) еще не ответили
				foreach (var comment in conversation)
				{
					// Условия: это чужой комментарий И в списке нет нашего ответа на него
					bool isOurs = comment.IsReplyOwnedByMe;
					if (isOurs) continue;

					bool alreadyReplied = conversation.Any(c => c.RepliedTo?.Id == comment.Id && c.IsReplyOwnedByMe);
					if (alreadyReplied) continue;

					// Проверяем в Redis, не стоит ли этот коммент уже в очереди на ответ
					var queuedKey = $"threads_queued:{comment.Id}";
					if (await _redis.KeyExistsAsync(queuedKey)) continue;

					// Помечаем в Redis, что мы взяли его в работу (на 1 час)
					await _redis.StringSetAsync(queuedKey, "queued", TimeSpan.FromMinutes(20));

					// КИДАЕМ В ОЧЕРЕДЬ
					await _publishEndpoint.Publish(new ThreadsProcessReply
					{
						BotId = bot.Id,
						ThreadsUserId = bot.ThreadsUserId ?? "",
						TargetMediaId = comment.Id,
						UserText = comment.Text ?? "",
						Username = comment.Username ?? "user"
					});

					await _console.Log($"Задание на ответ для @{comment.Username} отправлено в очередь.");

					//var aiResponse = await _aiService.GetAnswerAsync(bot.SystemPrompt, new List<AiRequest>
					//{
					//	new AiRequest { Role = "user", Text = comment.Text }
					//}, null);

					//if (string.IsNullOrWhiteSpace(aiResponse)) continue;

					//// 6. Отправляем ответ
					//var creationId = await _threadsService.CreateReplyContainerAsync(comment.Id, aiResponse, bot.AccessToken);
					//await _threadsService.PublishReplyAsync(creationId, bot.AccessToken);

					//_logger.LogInformation($"[ThreadsJob] Ответили пользователю @{comment.Username}");

					//// Небольшая пауза, чтобы не злить лимиты Meta
					//await Task.Delay(2000);
				}
			}
		}
		catch (Exception ex)
		{
			await _console.Log($"Ошибка обработки бота @{bot.Username}, {ex}");
		}
		finally
		{
			// 7. Обновляем время обработки, чтобы в следующий раз джоба взяла ДРУГОГО бота
			bot.LastProcessedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
		}
	}
}
