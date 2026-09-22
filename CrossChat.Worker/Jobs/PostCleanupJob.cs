using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Interfaces.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using StackExchange.Redis;
using Telegram.Bot.Types.ReplyMarkups;
using static CrossChat.Worker.Helpers.TimeZoneHelper;
using static CrossChat.Infrastructure.Constants.AppConstants;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class PostCleanupJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly IGoogleDriveUploader _driveUploader;
		private readonly ITelegramService _telegramService;
		private readonly ILogger<PostCleanupJob> _logger;
		private readonly IDatabase _redis;

		public PostCleanupJob(
			IServiceScopeFactory scopeFactory,
			IGoogleDriveUploader driveUploader,
			ITelegramService telegramService,
			IConnectionMultiplexer redis,
			IConfiguration configuration,
			ILogger<PostCleanupJob> logger)
		{
			_scopeFactory = scopeFactory;
			_driveUploader = driveUploader;
			_telegramService = telegramService;
			_logger = logger;
			_redis = redis.GetDatabase();
		}

		public async Task Execute(IJobExecutionContext context)
		{
			_logger.LogInformation("[PostCleanup] Запуск плановой очистки устаревших постов...");

			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			// Порог очистки: посты старше 30 дней
			var now = DateTimeNow;
			var thresholdDate = now.AddDays(-30);

			// ====================================================================
			// 1. УДАЛЕНИЕ УСПЕШНО ОПУБЛИКОВАННЫХ ПОСТОВ СТАРШЕ 30 ДНЕЙ
			// ====================================================================
			var oldPublishedPosts = await db.Posts
				.Include(p => p.Media)
				.Include(p => p.NetworkStates)
				.Where(p => p.ShowDate <= thresholdDate)
				// Проверяем: у поста есть активные соцсети, и ВСЕ они имеют статус Published!
				.Where(p => p.NetworkStates.Any(ns => ns.Status != (int)SocialStatus.None) &&
							p.NetworkStates.Where(ns => ns.Status != (int)SocialStatus.None)
										   .All(ns => ns.Status == (int)SocialStatus.Published))
				.Take(30) // Удаляем порциями по 30 постов за проход
				.ToListAsync();

			if (oldPublishedPosts.Any())
			{
				_logger.LogInformation("[PostCleanup] Найдено {Count} опубликованных постов старше 30 дней для очистки.", oldPublishedPosts.Count);

				foreach (var post in oldPublishedPosts)
				{
					try
					{
						// А. Удаляем все файлы и обложки из Google Диска
						foreach (var media in post.Media)
						{
							await _driveUploader.DeleteFileByIdAsync(media.GoogleDriveFileId);

							if (!string.IsNullOrEmpty(media.ThumbnailDriveFileId))
							{
								await _driveUploader.DeleteFileByIdAsync(media.ThumbnailDriveFileId);
							}
						}

						// Б. Удаляем связанные записи из базы данных
						db.PostMedia.RemoveRange(post.Media);
						db.NetworkStates.RemoveRange(post.NetworkStates);
						db.Posts.Remove(post);

						_logger.LogInformation("[PostCleanup] 🗑️ Успешно удален архивный пост {PostId} (дата: {Date:dd.MM.yyyy})", post.Id, post.ShowDate);
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "[PostCleanup] Ошибка при удалении архивного поста {PostId}", post.Id);
					}
				}

				await db.SaveChangesAsync();
			}

			// ====================================================================
			// 2. ОПОВЕЩЕНИЕ АДМИНА О ЗАВИСШИХ ПОСТАХ С ОШИБКОЙ СТАРШЕ 30 ДНЕЙ
			// ====================================================================
			var oldFailedPosts = await db.Posts
				.Include(p => p.Media)
				.Include(p => p.NetworkStates)
				.Include(p => p.Profile)
				.Where(p => p.ShowDate <= thresholdDate)
				// Есть хотя бы одна соцсеть в ошибке
				.Where(p => p.NetworkStates.Any(ns => ns.Status == (int)SocialStatus.Error))
				.Take(5)
				.ToListAsync();

			foreach (var post in oldFailedPosts)
			{
				try
				{
					// Защита от спама: отправляем уведомление по этому посту не чаще 1 раза в 7 дней
					string redisLockKey = $"alert:error_post:{post.Id}";
					bool shouldNotify = await _redis.StringSetAsync(redisLockKey, "1", TimeSpan.FromDays(7), When.NotExists);

					if (!shouldNotify) continue;

					// Собираем список сбойных соцсетей
					var failedNets = post.NetworkStates
						.Where(ns => ns.Status == (int)SocialStatus.Error)
						.Select(ns => ((NetworkType)ns.NetworkType).ToString());

					string networksStr = string.Join(", ", failedNets);
					string profileName = post.Profile?.Name ?? $"Профиль #{post.ProfileId}";

					string caption = post.NetworkStates.FirstOrDefault(ns => !string.IsNullOrEmpty(ns.Caption))?.Caption ?? "Без описания";
					if (caption.Length > 200) caption = caption.Substring(0, 197) + "...";

					string adminMessage =
						$"⚠️ <b>ЗАВИСШИЙ ПОСТ ТРЕБУЕТ ВНИМАНИЯ!</b>\n\n" +
						$"<b>Профиль:</b> {profileName}\n" +
						$"<b>Дата публикации:</b> {post.ShowDate:dd.MM.yyyy HH:mm}\n" +
						$"<b>Сбойные сети:</b> <code>{networksStr}</code>\n" +
						$"<b>Медиафайлов:</b> {post.Media.Count} шт.\n\n" +
						$"<b>Текст поста:</b>\n" +
						$"<i>«{caption}»</i>\n\n" +
						$"<i>ℹ️ Пост старше 30 дней и содержит ошибку. Он НЕ был удален и ожидает вашего решения.</i>";

					var inlineKeyboard = new InlineKeyboardMarkup(new[]
					{
						InlineKeyboardButton.WithUrl("📅 Открыть в Планировщике", $"{APP_URL}/planner?profileId={post.ProfileId}&network=All")
					});

					await _telegramService.SendMessageToAdmin(adminMessage, replyMarkup: inlineKeyboard);
					_logger.LogInformation("[PostCleanup] 📩 Оповещение о сбойном посте {PostId} отправлено админу в Telegram.", post.Id);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[PostCleanup] Не удалось отправить уведомление о сбойном посте {PostId}", post.Id);
				}
			}

			_logger.LogInformation("[PostCleanup] Плановая очистка завершена.");
		}
	}
}