using System.Text.Json;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

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

		public BlueSkyHashtagEngagementJob(
			AppDbContext db,
			IBlueSkyService bskyService,
			IBlueSkyTokenManager tokenManager,
			IBlueSkyConsole console,
			ILogger<BlueSkyHashtagEngagementJob> logger,
			IHostEnvironment env)
		{
			_db = db;
			_bskyService = bskyService;
			_tokenManager = tokenManager;
			_console = console;
			_logger = logger;
			_env = env;
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

					// Выбираем 1 случайный тег для этого цикла (чтобы активность была естественной)
					string selectedTag = tags[Random.Shared.Next(tags.Count)];
					await _console.Log($"🔍 [Радар] Поиск свежих постов по тегу «{selectedTag}» в BlueSky...", bot.UserId, bot.Id);

					// 2. Ищем 5 самых свежих постов по тегу
					var searchedPosts = await _bskyService.SearchPostsByQueryAsync(selectedTag, 5, botModel);
					if (!searchedPosts.Any())
					{
						await _console.Log($"[Радар] По тегу «{selectedTag}» свежих постов пока не найдено.", bot.UserId, bot.Id);
						continue;
					}

					// 3. Загружаем список уже прокомментированных URI постов
					var usedUris = new HashSet<string>();
					try
					{
						if (!string.IsNullOrEmpty(bot.UsedPostUrisJson))
						{
							usedUris = JsonSerializer.Deserialize<HashSet<string>>(bot.UsedPostUrisJson) ?? new HashSet<string>();
						}
					}
					catch { usedUris = new HashSet<string>(); }

					// Фильтруем: берем посты, которые мы еще НЕ комментировали
					var freshPosts = searchedPosts
						.Where(p => !usedUris.Contains(p.Uri))
						.ToList();

					if (!freshPosts.Any())
					{
						await _console.Log($"[Радар] Все найденные посты по тегу «{selectedTag}» уже были прокомментированы ранее.", bot.UserId, bot.Id);
						continue;
					}

					// Берем от 1 до 2 постов за проход (или до 3-4, если нужно)
					var postsToEngage = freshPosts.Take(2).ToList();
					int commentedSuccess = 0;

					for (int i = 0; i < postsToEngage.Count; i++)
					{
						var targetPost = postsToEngage[i];

						try
						{
							_logger.LogInformation("🎯 [BlueSky Radar] Обработка поста {Index}/{Total} от @{Author}:\n💬 Текст: «{Text}»\n🔗 Ссылка: {Link}",
								i + 1,
								postsToEngage.Count,
								targetPost.AuthorHandle,
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

								await _console.Log($"✅ Оставили комментарий под постом @{targetPost.AuthorHandle}: «{commentText}»\n🔗 {targetPost.WebPermalink}", bot.UserId, bot.Id);
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