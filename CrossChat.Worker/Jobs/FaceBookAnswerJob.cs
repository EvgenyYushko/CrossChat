using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class FaceBookAnswerJob : IJob
	{
		private IFaceBookService _fbService;
		private ILogger<FaceBookAnswerJob> _logger;
		private readonly AppDbContext _db;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IUserConsoleService _console;
		private readonly IDatabase _redis;

		public FaceBookAnswerJob(IFaceBookService fbService, ILogger<FaceBookAnswerJob> logger, AppDbContext db
			, IConnectionMultiplexer redis, IPublishEndpoint publishEndpoint, IUserConsoleService console)
		{
			_fbService = fbService;
			_logger = logger;
			_db = db;
			_publishEndpoint = publishEndpoint;
			_console = console;
			_redis = redis.GetDatabase();
		}

		public async Task Execute(IJobExecutionContext context)
		{
			try
			{
				var activeBots = await _db.FacebookSettings
					.Where(s => s.IsActive)
					.ToListAsync();

				foreach (var bot in activeBots)
				{
					await _console.WriteLogAsync(bot.UserId, "facebook", bot.Id, $"[FaceBookAnswerJob] Проверка аккаунта @{bot.PageName}", "facebook");
					_logger.LogInformation($"[FaceBookAnswerJob] Проверка аккаунта @{bot.PageName}");

					// 1. Получаем диалоги, на которые нужно ответить
					var incomingDialogs = await _fbService.GetUnreadDialogsAsync(bot.PageAccessToken, bot.PageId);

					if (incomingDialogs == null || !incomingDialogs.Any()) return;

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

							_logger.LogInformation($"[FaceBookScanner] Чат {dlg.id} для @{bot.PageName} отправлен в очередь.");
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError($"Ошибка в FaceBookDmJob: {ex.Message}");
			}
		}
	}
}
