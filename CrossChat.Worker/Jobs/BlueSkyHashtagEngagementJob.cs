using System.Text.Json;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class BlueSkyHashtagEngagementJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IBlueSkyTokenManager _tokenManager;
		private readonly IBlueSkyConsole _console;
		private readonly ILogger<BlueSkyHashtagEngagementJob> _logger;
		private readonly IHostEnvironment _env;
		private readonly IDatabase _redis;

		public BlueSkyHashtagEngagementJob(
			AppDbContext db,
			IBlueSkyService bskyService,
			IBlueSkyTokenManager tokenManager,
			IBlueSkyConsole console,
			IConnectionMultiplexer redis,
			ILogger<BlueSkyHashtagEngagementJob> logger,
			IHostEnvironment env)
		{
			_db = db;
			_bskyService = bskyService;
			_tokenManager = tokenManager;
			_console = console;
			_logger = logger;
			_env = env;
			_redis = redis.GetDatabase();
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			_logger.LogInformation("🚀 [BlueSky Radar] Запуск планового обхода и комментирования постов по хештегам...");

			// Ищем ботов с включенным автокомментингом
			var activeBots = await _db.BlueSkySettings
				.Where(s => s.AccessToken != null && s.IsHashtagEngagementEnabled && !string.IsNullOrEmpty(s.EngagementHashtags))
				.ToListAsync();

			if (!activeBots.Any())
			{
				_logger.LogInformation("[BlueSky Radar] Нет активных аккаунтов с включенным авто-радаром.");
				return;
			}

			foreach (var bot in activeBots)
			{
				try
				{
					var botModel = await _tokenManager.GetValidTokenAsync(bot.Id);
					if (botModel == null) continue;

					// 1. Разбиваем хештеги (напр. "#fitness, #smm, #marketing")
					var tags = bot.EngagementHashtags!
						.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
						.Select(t => t.Trim())
						.Where(t => t.Length > 1)
						.ToList();

					if (!tags.Any()) continue;

					// 2. Загружаем список уже прокомментированных URI постов (до поиска)
					var usedUris = new HashSet<string>();
					try
					{
						if (!string.IsNullOrEmpty(bot.UsedPostUrisJson))
						{
							usedUris = JsonSerializer.Deserialize<HashSet<string>>(bot.UsedPostUrisJson) ?? new HashSet<string>();
						}
					}
					catch { usedUris = new HashSet<string>(); }

					// 3. Достаем текущую позицию очереди из Redis
					var redisTagKey = $"bsky:radar:tag_idx:{bot.Id}";
					long currentIndex = 0;

					var storedIndex = await _redis.StringGetAsync(redisTagKey);
					if (storedIndex.HasValue && long.TryParse(storedIndex, out long lastIdx))
					{
						currentIndex = (lastIdx + 1) % tags.Count;
					}

					List<BlueSkySearchedPostDto> freshPosts = new();
					string selectedTag = "";
					int attempts = 0;
					// Лимит попыток за один запуск (чтобы не спамить API в пустую, если все 91 тег пустые)
					int maxSearchAttempts = Math.Min(tags.Count, 25);

					// =========================================================================
					// 🚀 СКВОЗНОЙ ПОИСК: ПЕРЕБИРАЕМ ТЕГИ ПО КРУГУ, ПОКА НЕ НАЙДЕМ ЖИВЫЕ ПОСТЫ
					// =========================================================================
					while (attempts < maxSearchAttempts)
					{
						selectedTag = tags[(int)currentIndex];
						attempts++;

						_logger.LogInformation("🎯 [BlueSky Radar] Очередь тегов: шаг {Step}/{Total} для @{Handle}. Тег: «{Tag}» (попытка {Att}/{Max})",
							currentIndex + 1, tags.Count, bot.Handle, selectedTag, attempts, maxSearchAttempts);

						await _console.Log($"🔍 [Радар] Шаг {currentIndex + 1}/{tags.Count}: поиск по тегу «{selectedTag}»...", bot.UserId, bot.Id);

						// Ищем посты по тегу (с нашим фильтром свежего топа за 48ч)
						var searchedPosts = await _bskyService.SearchPostsByQueryAsync(selectedTag, 5, botModel);

						// Отбираем посты, которые мы еще НЕ комментировали
						freshPosts = searchedPosts
							.Where(p => !usedUris.Contains(p.Uri))
							.ToList();

						if (freshPosts.Any())
						{
							// ✅ НАШЛИ ЖИВЫЕ ПОСТЫ! Запоминаем текущий индекс в Redis и выходим из поиска
							await _redis.StringSetAsync(redisTagKey, currentIndex, TimeSpan.FromDays(60));
							break;
						}

						// ⚠️ Постов нет: сдвигаем очередь на следующий тег и пробуем снова!
						_logger.LogInformation("⏩ [BlueSky Radar] По тегу «{Tag}» нет свежих постов. Сразу переходим к следующему...", selectedTag);
						await _console.Log($"[Радар] По тегу «{selectedTag}» свежих постов нет. Проверяем следующий тег...", bot.UserId, bot.Id);

						currentIndex = (currentIndex + 1) % tags.Count;
						await _redis.StringSetAsync(redisTagKey, currentIndex, TimeSpan.FromDays(60));

						// Небольшая микро-пауза 500 мс между поисковыми запросами к API
						await Task.Delay(500);
					}

					if (!freshPosts.Any())
					{
						await _console.Log($"[Радар] Проверено {attempts} тегов, но свежих постов для комментирования пока не найдено.", bot.UserId, bot.Id);
						continue;
					}

					// 4. БЕРЕМ ТОП-2 САМЫХ ПОПУЛЯРНЫХ СВЕЖИХ ПОСТА ДЛЯ КОММЕНТИРОВАНИЯ:
					var postsToEngage = freshPosts.OrderByDescending(p => p.LikeCount).Take(2).ToList();
					int commentedSuccess = 0;

					for (int i = 0; i < postsToEngage.Count; i++)
					{
						var targetPost = postsToEngage[i];

						try
						{
							_logger.LogInformation("🎯 [BlueSky Radar] Комментируем ТОП-пост {Index}/{Total} от @{Author} (❤️ {Likes} лайков, 🔁 {Reposts} репостов):\n💬 Текст: «{Text}»\n🔗 Ссылка: {Link}",
								i + 1,
								postsToEngage.Count,
								targetPost.AuthorHandle,
								targetPost.LikeCount,
								targetPost.RepostCount,
								targetPost.Text.Length > 50 ? targetPost.Text.Substring(0, 47) + "..." : targetPost.Text,
								targetPost.WebPermalink);

							// =========================================================================
							// 1. СТАВИМ ЛАЙК ПОСТУ ❤️ (Первое касание автора)
							// =========================================================================
							bool likeOk = await _bskyService.LikePostAsync(targetPost.Uri, targetPost.Cid, botModel);
							if (likeOk)
							{
								await _console.Log($"❤️ Поставили лайк посту @{targetPost.AuthorHandle}", bot.UserId, bot.Id);
							}

							// Микро-пауза 5–8 секунд (как будто человек прочитал пост и нажал кнопку ответа)
							await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 9)));

							// =========================================================================
							// 2. ГЕНЕРИРУЕМ И ПУБЛИКУЕМ КОММЕНТАРИЙ 💬
							// =========================================================================
							string commentText = !string.IsNullOrWhiteSpace(bot.EngagementCommentTemplates)
								? InstagramCommentEngine.GetRandomTemplate(bot.EngagementCommentTemplates)
								: "Great post! ✨";

							bool isSuccess = await _bskyService.ReplyToThreadCommentAsync(
								commentText,
								targetPost.Uri,
								targetPost.Cid,
								targetPost.RootUri,
								targetPost.RootCid,
								botModel);

							if (isSuccess)
							{
								commentedSuccess++;
								usedUris.Add(targetPost.Uri);

								await _console.Log($"✅ Оставили комментарий под ТОП-постом @{targetPost.AuthorHandle} (❤️ {targetPost.LikeCount} лайков): «{commentText}»\n🔗 {targetPost.WebPermalink}", bot.UserId, bot.Id);
							}
							else
							{
								_logger.LogWarning("⚠️ [BlueSky Radar] Не удалось оставить комментарий под постом {Uri} (Threadgate / закрытые ответы).", targetPost.Uri);
								usedUris.Add(targetPost.Uri);
							}

							// =========================================================================
							// 3. БЕЗОПАСНАЯ ПАУЗА МЕЖДУ РАЗНЫМИ ПОСТАМИ (35–65 СЕКУНД)
							// =========================================================================
							// Делаем паузу только если это НЕ последний пост в списке
							if (i < postsToEngage.Count - 1)
							{
								int safeWaitSeconds = Random.Shared.Next(35, 66);
								_logger.LogInformation("⏳ [BlueSky Radar] Безопасная пауза перед следующим постом: {Sec} сек...", safeWaitSeconds);
								await _console.Log($"⏳ [Радар] Отдых {safeWaitSeconds}с перед следующим постом...", bot.UserId, bot.Id);

								await Task.Delay(TimeSpan.FromSeconds(safeWaitSeconds));
							}
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при обработке поста {Uri}", targetPost.Uri);
						}
					}

					// 6. Сохраняем обновленный кэш (храним последние 300 постов, чтобы не раздувать БД)
					if (usedUris.Count > 300)
					{
						usedUris = usedUris.TakeLast(300).ToHashSet();
					}

					bot.UsedPostUrisJson = JsonSerializer.Serialize(usedUris);
					bot.LastHashtagEngagementAt = DateTime.UtcNow;
					await _db.SaveChangesAsync();

					_logger.LogInformation("🏁 [BlueSky Radar] Цикл для @{Handle} завершен. Успешно оставлено комментариев: {Count}", bot.Handle, commentedSuccess);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Критическая ошибка авто-радара для бота ID {Id}", bot.Id);
				}
			}
		}
	}
}