using System.Text.Json;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Consumers.Instagram; // Для Spintax-генератора
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

					// Берем от 1 до 2 постов за проход (безопасный органический темп)
					var postsToEngage = freshPosts.Take(2).ToList();
					int commentedSuccess = 0;

					foreach (var targetPost in postsToEngage)
					{
						try
						{
							// 4. Генерируем живой текст комментария через Spintax-шаблон
							string commentText = !string.IsNullOrWhiteSpace(bot.EngagementCommentTemplates)
								? InstagramCommentEngine.GetRandomTemplate(bot.EngagementCommentTemplates)
								: "Great post! ✨";

							// 📋 ПОДРОБНЫЙ ЛОГ: выводим ссылку на пост прямо в консоль и дашборд!
							_logger.LogInformation("🎯 [BlueSky Radar] Комментируем пост @{Author}:\n💬 Текст поста: «{Text}»\n🔗 Ссылка: {Link}\n✍️ Наш ответ: «{Reply}»",
								targetPost.AuthorHandle,
								targetPost.Text.Length > 50 ? targetPost.Text.Substring(0, 47) + "..." : targetPost.Text,
								targetPost.WebPermalink,
								commentText);

							// Пауза 15–25 секунд (имитация чтения и набора текста живым человеком)
							int humanDelay = Random.Shared.Next(15, 25);
							await Task.Delay(TimeSpan.FromSeconds(humanDelay));

							// 5. Публикуем комментарий в ветку поста через официальный XRPC
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
								_logger.LogWarning("⚠️ [BlueSky Radar] Не удалось оставить комментарий под постом {Uri} (возможно, автор ограничил ответы - Threadgate).", targetPost.Uri);
								// Вносим в список использованных, чтобы не долбиться в закрытый пост снова
								usedUris.Add(targetPost.Uri);
							}
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при комментировании поста {Uri}", targetPost.Uri);
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