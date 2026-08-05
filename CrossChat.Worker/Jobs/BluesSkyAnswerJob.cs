using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Quartz;
using StackExchange.Redis;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Jobs
{
	public class BluesSkyAnswerJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IBlueSkyConsole _console;
		private readonly IDatabase _redis;

		public BluesSkyAnswerJob(AppDbContext db,
			IBlueSkyService blueSkyService,
			IPublishEndpoint publishEndpoint,
			IConnectionMultiplexer redis,
			IBlueSkyConsole consoleService)
		{
			_db = db;
			_bskyService = blueSkyService;
			_redis = redis.GetDatabase();
			_publishEndpoint = publishEndpoint;
			_console = consoleService;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var activeBots = await _db.BlueSkySettings
				.Where(s => s.IsActive && s.AccessToken != null)
				.ToListAsync();

			foreach (var bot in activeBots)
			{
				try
				{
					// Проверяем: если дата истечения не задана ИЛИ до конца жизни токена осталось меньше 5 минут
					bool isTokenExpired = !bot.TokenExpiresAt.HasValue ||
										  bot.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(5);

					if (isTokenExpired)
					{
						await _console.Log($"Токен для @{bot.Handle} истек или скоро истечет. Обновляем...", bot.UserId, bot.Id);

						if (string.IsNullOrEmpty(bot.RefreshToken))
						{
							await _console.LogError($"[BlueSky] Ошибка: отсутствует RefreshToken для @{bot.Handle}", bot.UserId, bot.Id);
							continue; // Пропускаем бота, так как без RefreshToken обновить токен невозможно
						}

						// Запрашиваем новый токен через сервис
						var result = await _bskyService.RefreshTokenAsync(bot.RefreshToken, bot.PrivateKeyJson!);

						if (result == null)
						{
							await _console.LogError($"[BlueSky] Не удалось обновить токен для @{bot.Handle}", bot.UserId, bot.Id);
							continue;
						}

						// ОБНОВЛЯЕМ поля в БД сущности
						bot.AccessToken = result.Value.AccessToken;
						bot.RefreshToken = result.Value.RefreshToken;
						bot.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);

						// ЯВНО сохраняем обновленный токен в PostgreSQL!
						await _db.SaveChangesAsync();

						await _console.Log($"Токен успешно обновлен. Новый срок: {bot.TokenExpiresAt}", bot.UserId, bot.Id);
					}

					// Собираем свежую модель с актуальным токеном
					var botModel = new BlueSkyModel
					{
						AccessToken = bot.AccessToken,
						RefreshToken = bot.RefreshToken,
						Handle = bot.Handle,
						PrivateKeyJson = bot.PrivateKeyJson,
						TokenExpiresAt = bot.TokenExpiresAt,
						Did = bot.Did,
						PdsUrl = bot.PdsUrl
					};

					// 2. Получаем непрочитанные диалоги с использованием свежего токена
					var unreadConvos = await _bskyService.GetUnreadConversationsAsync(botModel);

					if (unreadConvos != null)
					{
						foreach (var convo in unreadConvos)
						{
							// Если последнее сообщение от нас — пропускаем
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

								await _console.Log($"Чат {convo.Id} для @{bot.Handle} отправлен в очередь.", bot.UserId, bot.Id);
							}
						}
					}
				}
				catch (Exception ex)
				{
					await _console.LogError($"Критическая ошибка обработки бота {bot.Handle}: {ex.Message}", bot.UserId, bot.Id);
				}
			}
		}
	}
}
