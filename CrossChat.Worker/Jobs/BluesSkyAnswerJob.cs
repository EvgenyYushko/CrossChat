using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
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

        public BluesSkyAnswerJob(
            AppDbContext db,
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
                    // 1. БЕЗОПАСНАЯ АКТУАЛИЗАЦИЯ ТОКЕНА ЧЕРЕЗ REDIS LOCK
                    var isTokenReady = await EnsureBotTokenValidWithLockAsync(bot);
                    if (!isTokenReady)
                    {
                        await _console.LogError($"[BlueSky] Пропуск обработки @{bot.Handle}: не удалось получить валидный токен.", bot.UserId, bot.Id);
                        continue;
                    }

                    // Собираем свежую рабочую модель
                    var botModel = new BlueSkyModel
                    {
                        AccessToken = bot.AccessToken!,
                        RefreshToken = bot.RefreshToken,
                        Handle = bot.Handle,
                        PrivateKeyJson = bot.PrivateKeyJson!,
                        TokenExpiresAt = bot.TokenExpiresAt,
                        Did = bot.Did!,
                        PdsUrl = bot.PdsUrl!
                    };

                    // ====================================================================
                    // 2. ОБРАБОТКА ДИАЛОГОВ (DIRECT MESSAGES)
                    // ====================================================================
                    if (bot.IsDirectEnabled)
                    {
                        var unreadConvos = await _bskyService.GetUnreadConversationsAsync(botModel);

                        if (unreadConvos != null)
                        {
                            foreach (var convo in unreadConvos)
                            {
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
                    // 3. ОБРАБОТКА КОММЕНТАРИЕВ И УПОМИНАНИЙ (REPLIES & MENTIONS)
                    // ====================================================================
                    if (bot.IsCommentsEnabled)
                    {
                        var notifications = await _bskyService.GetUnreadNotificationsAsync(botModel);

                        if (notifications != null && notifications.Any())
                        {
                            DateTime lastProcessedUtc = bot.LastProcessedAt.HasValue
                                ? DateTime.SpecifyKind(bot.LastProcessedAt.Value, DateTimeKind.Utc)
                                : DateTime.UtcNow.AddMinutes(-30);

                            var newComments = notifications
                                .Where(n => DateTimeOffset.TryParse(n.IndexedAt, out var dto) && dto.UtcDateTime > lastProcessedUtc)
                                .Where(n => n.Author.Did != botModel.Did)
                                .OrderBy(n => DateTimeOffset.Parse(n.IndexedAt).UtcDateTime)
                                .ToList();

                            if (newComments.Any())
                            {
                                await _console.Log($"[BlueSky] Найдено {newComments.Count} новых комментариев, созданных после {lastProcessedUtc:HH:mm:ss} UTC!", bot.UserId, bot.Id);

                                DateTime maxIndexedAtUtc = lastProcessedUtc;

                                foreach (var notif in newComments)
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

                                    await _console.Log($"Комментарий от @{notif.Author.Handle}: «{text}» отправлен в очередь ответов.", bot.UserId, bot.Id);

                                    if (DateTimeOffset.TryParse(notif.IndexedAt, out var dto) && dto.UtcDateTime > maxIndexedAtUtc)
                                    {
                                        maxIndexedAtUtc = dto.UtcDateTime;
                                    }
                                }

                                bot.LastProcessedAt = maxIndexedAtUtc;
                                await _db.SaveChangesAsync();

                                await _bskyService.UpdateNotificationsSeenAsync(botModel, maxIndexedAtUtc);
                            }
                            else
                            {
                                await _console.Log($"[BlueSky] Все комментарии уже обработаны ранее (до {lastProcessedUtc:HH:mm:ss} UTC).", bot.UserId, bot.Id);
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

        /// <summary>
        /// Потокобезопасная проверка и обновление токена бота с использованием распределенного Redis Lock
        /// </summary>
        private async Task<bool> EnsureBotTokenValidWithLockAsync(BlueSkySettings bot)
        {
            // Если токен еще свежий (действует больше 15 мин) — пропускаем без взятия блокировок
            if (bot.TokenExpiresAt.HasValue && bot.TokenExpiresAt.Value > DateTimeNow.AddMinutes(15))
            {
                return true;
            }

            var lockKey = $"lock:bsky_token_refresh:{bot.Id}";
            var lockTokenValue = Guid.NewGuid().ToString("N");

            // Пытаемся захватить лок на 30 секунд
            bool isLockAcquired = await _redis.StringSetAsync(lockKey, lockTokenValue, TimeSpan.FromSeconds(30), When.NotExists);

            if (isLockAcquired)
            {
                try
                {
                    // ВАЖНО: Перечитываем запись из базы данных!
                    // Пока мы ждали или брали лок, соседний поток/контейнер мог уже обновить токен.
                    await _db.Entry(bot).ReloadAsync();

                    // Проверяем снова после перезагрузки
                    if (bot.TokenExpiresAt.HasValue && bot.TokenExpiresAt.Value > DateTimeNow.AddMinutes(15))
                    {
                        return true;
                    }

                    var tempModel = new BlueSkyModel
                    {
                        AccessToken = bot.AccessToken!,
                        RefreshToken = bot.RefreshToken,
                        Handle = bot.Handle,
                        PrivateKeyJson = bot.PrivateKeyJson!,
                        TokenExpiresAt = bot.TokenExpiresAt,
                        Did = bot.Did!,
                        PdsUrl = bot.PdsUrl!
                    };

                    // ЕДИНАЯ ТОЧКА ОБНОВЛЕНИЯ:
                    await _bskyService.GetValidTokenAsync(tempModel);

                    // Синхронизируем новые токены обратно в EF Core сущность
                    bot.AccessToken = tempModel.AccessToken;
                    bot.RefreshToken = tempModel.RefreshToken;
                    bot.TokenExpiresAt = tempModel.TokenExpiresAt;

                    await _db.SaveChangesAsync();

                    await _console.Log($"[BlueSky] Токен успешно обновлен через Lock. Новый срок: {bot.TokenExpiresAt}", bot.UserId, bot.Id);
                    return true;
                }
                catch (Exception ex)
                {
                    await _console.LogError($"[BlueSky] Ошибка при обновлении токена через Lock: {ex.Message}", bot.UserId, bot.Id);
                    return false;
                }
                finally
                {
                    // Освобождаем лок только если он все еще принадлежит нам
                    var currentLock = await _redis.StringGetAsync(lockKey);
                    if (currentLock == lockTokenValue)
                    {
                        await _redis.KeyDeleteAsync(lockKey);
                    }
                }
            }
            else
            {
                // Если лок занят другим контейнером/джобы прямо сейчас — ждем 2.5 сек и подтягиваем свежие данные из базы
                await _console.Log($"[BlueSky] Токен для @{bot.Handle} сейчас обновляется другим процессом. Ожидаем завершения...", bot.UserId, bot.Id);
                await Task.Delay(2500);

                await _db.Entry(bot).ReloadAsync();
                return bot.TokenExpiresAt.HasValue && bot.TokenExpiresAt.Value > DateTimeNow;
            }
        }
    }
}