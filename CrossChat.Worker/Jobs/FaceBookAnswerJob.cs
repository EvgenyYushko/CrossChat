using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class FaceBookAnswerJob : IJob
	{
		private IFaceBookService _fbService;
		private readonly AppDbContext _db;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IFaceBookConsole _console;
		private readonly IHostEnvironment _env;
		private readonly IDatabase _redis;

		public FaceBookAnswerJob(IFaceBookService fbService, AppDbContext db
			, IConnectionMultiplexer redis, IPublishEndpoint publishEndpoint, IFaceBookConsole console, IHostEnvironment env)
		{
			_fbService = fbService;
			_db = db;
			_publishEndpoint = publishEndpoint;
			_console = console;
			_env = env;
			_redis = redis.GetDatabase();
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			try
			{
				var activeBots = await _db.FacebookSettings
					.Where(s => s.IsActive)
					.ToListAsync();

				foreach (var bot in activeBots)
				{
					await _console.Log($"Проверка аккаунта @{bot.PageName}", bot.UserId, bot.Id);

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

							await _console.Log($"Чат {dlg.id} для @{bot.PageName} отправлен в очередь.");
						}
					}
				}
			}
			catch (Exception ex)
			{
				await _console.LogError($"Ошибка в FaceBookDmJob: {ex.Message}");
			}
		}
	}
}
