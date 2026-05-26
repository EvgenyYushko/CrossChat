using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs
{
	public class BluesSkyAnswerJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly ILogger<BluesSkyAnswerJob> _logger;
		private readonly IBlueSkyService _bskyService;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IUserConsoleService _consoleService;
		private readonly IDatabase _redis;

		public BluesSkyAnswerJob(AppDbContext db
			, ILogger<BluesSkyAnswerJob> logger
			, IBlueSkyService blueSkyService
			, IPublishEndpoint publishEndpoint
			, IConnectionMultiplexer redis
			, IUserConsoleService userConsoleService)
		{
			_db = db;
			_logger = logger;
			_bskyService = blueSkyService;
			_redis = redis.GetDatabase();
			_publishEndpoint = publishEndpoint;
			_consoleService = userConsoleService;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var activeBots = await _db.BlueSkySettings
				.Where(s => s.IsActive && s.AccessToken != null)
				.ToListAsync();

			foreach (var bot in activeBots)
			{
				await _consoleService.WriteLogAsync(bot.UserId, "bluesky", bot.Id, $"[BluesJob] Проверка аккаунта @{bot.Handle}", "bluesky");
				_logger.LogInformation($"[BluesJob] Проверка аккаунта @{bot.Handle}");

				try
				{
					var botModel = new BlueSkyModel()
					{
						AccessToken = bot.AccessToken,
						RefreshToken = bot.RefreshToken,
						Handle = bot.Handle,
						PrivateKeyJson = bot.PrivateKeyJson,
						TokenExpiresAt = bot.TokenExpiresAt,
						Did = bot.Did,
						PdsUrl = bot.PdsUrl
					};

					var token = "";
					if (botModel.TokenExpiresAt.HasValue && botModel.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(2))
					{
						token = botModel.AccessToken!;
					}

					_logger.LogInformation($"[BlueSky] Токен для @{botModel.Handle} истек. Обновляем...");

					if (string.IsNullOrEmpty(token))
					{
						var result = await _bskyService.RefreshTokenAsync(botModel.RefreshToken!, botModel.PrivateKeyJson!);

						if (result == null)
						{
							throw new Exception("Ошибка при попытке обновить токен");
						}

						// 3. ОБЯЗАТЕЛЬНО обновляем объект в памяти
						bot.AccessToken = result.Value.AccessToken;
						bot.RefreshToken = result.Value.RefreshToken;
						bot.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);

						// 4. Сохраняем в БД (нужно будет вызвать _db.SaveChangesAsync() в вызывающем коде)
						// Но лучше передать сюда callback или сделать метод сохранения
						_logger.LogInformation($"[BlueSky] Токен успешно обновлен. Новый срок: {bot.TokenExpiresAt}");

						token = bot.AccessToken;
					}

					if (_db.Entry(bot).State == EntityState.Modified)
						await _db.SaveChangesAsync();

					botModel.AccessToken = bot.AccessToken;
					botModel.RefreshToken = bot.RefreshToken;
					botModel.TokenExpiresAt = bot.TokenExpiresAt;

					// 3. Получаем непрочитанные диалоги
					var unreadConvos = await _bskyService.GetUnreadConversationsAsync(botModel);

					foreach (var convo in unreadConvos)
					{
						// Если последнее сообщение от нас — просто читаем и уходим
						if (convo.LastMessage?.Sender.Did == botModel.Did)
						{
							continue;
						}

						var queueLockKey = $"lock:bsky_queued:{convo.Id}";
						if (await _redis.StringSetAsync(queueLockKey, "1", TimeSpan.FromMinutes(10), When.NotExists))
						{
							await _publishEndpoint.Publish(new BlueSkyProcessReply
							{
								BotDbId = bot.Id,
								ConvoId = convo.Id
							});

							_logger.LogInformation($"[BlueSkyScanner] Чат {convo.Id} для @{bot.Handle} отправлен в очередь.");
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, $"Ошибка обработки бота {bot.Handle}");
				}
			}
		}
	}
}
