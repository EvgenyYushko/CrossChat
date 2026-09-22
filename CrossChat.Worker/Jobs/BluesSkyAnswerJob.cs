using System.Text.Json;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
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
		private readonly IHostEnvironment _env;
		private readonly IDatabase _redis;

		public BluesSkyAnswerJob(AppDbContext db,
			IBlueSkyService blueSkyService,
			IPublishEndpoint publishEndpoint,
			IConnectionMultiplexer redis,
			IBlueSkyConsole consoleService,
			IHostEnvironment env)
		{
			_db = db;
			_bskyService = blueSkyService;
			_redis = redis.GetDatabase();
			_publishEndpoint = publishEndpoint;
			_console = consoleService;
			_env = env;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			var activeBots = await _db.BlueSkySettings
				.Where(s => s.AccessToken != null)
				.ToListAsync();

			foreach (var bot in activeBots)
			{
				try
				{
					// Учитываем интервал запуска джобы (30 мин) + 5 минут запаса.
					// Если токен истечет в течение ближайших 35 минут (до следующего захода джобы), 
					// обновляем его прямо сейчас!
					bool isTokenExpired = !bot.TokenExpiresAt.HasValue ||
										  bot.TokenExpiresAt.Value <= DateTimeNow.AddMinutes(35);

					if (isTokenExpired)
					{
						await _console.Log($"Токен для @{bot.Handle} истекает в ближайшие 35 мин. Обновляем заранее...", bot.UserId, bot.Id);

						if (string.IsNullOrEmpty(bot.RefreshToken))
						{
							await _console.LogError($"[BlueSky] Ошибка: отсутствует RefreshToken для @{bot.Handle}", bot.UserId, bot.Id);
							continue;
						}

						var result = await _bskyService.RefreshTokenAsync(bot.RefreshToken, bot.PrivateKeyJson!);

						if (result == null)
						{
							await _console.LogError($"[BlueSky] Не удалось обновить токен для @{bot.Handle}", bot.UserId, bot.Id);
							continue;
						}

						bot.AccessToken = result.Value.AccessToken;
						bot.RefreshToken = result.Value.RefreshToken;
						bot.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);

						await _db.SaveChangesAsync();

						await _console.Log($"Токен успешно обновлен. Новый срок истечения: {bot.TokenExpiresAt}", bot.UserId, bot.Id);
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

					if (bot.IsDirectEnabled)
					{
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

					// ====================================================================
					// 2. ОБРАБОТКА КОММЕНТАРИЕВ И УПОМИНАНИЙ (REPLIES & MENTIONS)
					// ====================================================================
					if (bot.IsCommentsEnabled)
					{
						var notifications = await _bskyService.GetUnreadNotificationsAsync(botModel);

						if (notifications != null && notifications.Any())
						{
							await _console.Log($"Найдено {notifications.Count} новых уведомлений для @{bot.Handle}!", bot.UserId, bot.Id);

							DateTime maxIndexedAt = DateTime.MinValue;

							foreach (var notif in notifications)
							{
								// Игнорируем свои собственные сообщения
								if (notif.Author.Did == botModel.Did) continue;

								// Защита от дублей в Redis (на 2 часа)
								var lockKey = $"lock:bsky_comment:{notif.Cid}";

								// ИСПРАВЛЕНИЕ: здесь должен быть lockKey, а не queueLockKey!
								if (await _redis.StringSetAsync(lockKey, "1", TimeSpan.FromHours(2), When.NotExists))
								{
									string text = "";
									string rootUri = notif.Uri;
									string rootCid = notif.Cid;

									if (notif.Record != null)
									{
										var recordJson = JsonSerializer.Serialize(notif.Record);
										using var doc = JsonDocument.Parse(recordJson);

										if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString() ?? "";

										if (doc.RootElement.TryGetProperty("reply", out var rep))
										{
											if (rep.TryGetProperty("root", out var r) && r.TryGetProperty("uri", out var ru) && r.TryGetProperty("cid", out var rc))
											{
												rootUri = ru.GetString() ?? notif.Uri;
												rootCid = rc.GetString() ?? notif.Cid;
											}
										}
									}

									// Отправляем в очередь MassTransit
									await _publishEndpoint.Publish(new BlueSkyCommentReceived
									{
										BotDbId = bot.Id,
										CommentUri = notif.Uri,
										CommentCid = notif.Cid,
										RootUri = rootUri,
										RootCid = rootCid,
										AuthorDid = notif.Author.Did,
										AuthorHandle = notif.Author.Handle,
										Text = text
									});

									await _console.Log($"Комментарий от @{notif.Author.Handle} отправлен в очередь ответов.", bot.UserId, bot.Id);
								}

								if (DateTime.TryParse(notif.IndexedAt, out var dt) && dt > maxIndexedAt)
								{
									maxIndexedAt = dt;
								}
							}

							// Помечаем все обработанные уведомления прочитанными на сервере BlueSky
							if (maxIndexedAt > DateTime.MinValue)
							{
								await _bskyService.UpdateNotificationsSeenAsync(botModel, maxIndexedAt);
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
