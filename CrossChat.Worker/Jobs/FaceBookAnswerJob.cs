using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class FaceBookAnswerJob : IJob
	{
		private readonly IFaceBookService _fbService;
		private readonly AppDbContext _db;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IFaceBookConsole _console;
		private readonly IHostEnvironment _env;
		private readonly IDatabase _redis;
		private readonly ILogger<FaceBookAnswerJob> _logger;

		public FaceBookAnswerJob(
			IFaceBookService fbService, 
			AppDbContext db,
			IConnectionMultiplexer redis, 
			IPublishEndpoint publishEndpoint, 
			IFaceBookConsole console, 
			IHostEnvironment env,
			ILogger<FaceBookAnswerJob> logger)
		{
			_fbService = fbService;
			_db = db;
			_publishEndpoint = publishEndpoint;
			_console = console;
			_env = env;
			_logger = logger;
			_redis = redis.GetDatabase();
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			var activeBots = await _db.FacebookSettings
				.Where(s => s.IsActive && !string.IsNullOrEmpty(s.PageAccessToken))
				.ToListAsync();

			foreach (var bot in activeBots)
			{
				try
				{
					await _console.Log($"Проверка аккаунта {bot.PageName}", bot.UserId, bot.Id);

					// 1. Получаем диалоги, на которые нужно ответить
					var incomingDialogs = await _fbService.GetUnreadDialogsAsync(bot.PageAccessToken, bot.PageId);

					if (incomingDialogs == null || !incomingDialogs.Any()) 
						continue;

					foreach (var dlg in incomingDialogs)
					{
						var queueLockKey = $"lock:fsbk_queued:{dlg.id}";
						if (await _redis.StringSetAsync(queueLockKey, "1", TimeSpan.FromMinutes(10), When.NotExists))
						{
							await _publishEndpoint.Publish(new FaceBookProcessReply
							{
								BotDbId = bot.Id,
								DialogId = dlg.id
							});

							// ИСПРАВЛЕНИЕ: Обязательно передаем bot.UserId и bot.Id!
							await _console.Log($"Чат {dlg.id} для @{bot.PageName} отправлен в очередь.", bot.UserId, bot.Id);
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, $"Ошибка обработки бота Facebook {bot.PageName} ({bot.Id})");
					await _console.LogError($"Ошибка обработки страницы @{bot.PageName}: {ex.Message}", bot.UserId, bot.Id);
				}
			}
		}
	}
}