using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces.Google;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Helpers
{
	public static class PostCleanupHelper
	{
		/// <summary>
		/// Единая логика умного удаления связанных постов для любой соцсети.
		/// Удаляет одиночные посты и чистит Google Drive, а в мультипостах лишь отвязывает сеть.
		/// </summary>
		public static async Task CleanupLinkedPostsAsync(
			AppDbContext db,
			IGoogleDriveUploader? uploader,
			NetworkType networkType,
			int botId,
			ILogger? logger = null)
		{
			try
			{
				logger?.LogInformation("🧹 [Cleanup] Очистка постов для сети {Net} (BotId: {BotId})...", networkType, botId);

				var linkedStates = await db.NetworkStates
					.Include(ns => ns.Post)
						.ThenInclude(p => p.Media)
					.Include(ns => ns.Post)
						.ThenInclude(p => p.NetworkStates)
					.Where(ns => ns.NetworkType == (int)networkType && ns.BotId == botId)
					.ToListAsync();

				if (!linkedStates.Any()) return;

				var postsToCheck = linkedStates.Select(ns => ns.Post).Distinct().ToList();

				foreach (var post in postsToCheck)
				{
					// Удаляем состояние конкретно для этого бота
					var targetState = post.NetworkStates
						.FirstOrDefault(ns => ns.NetworkType == (int)networkType && ns.BotId == botId);

					if (targetState != null)
					{
						db.NetworkStates.Remove(targetState);
					}

					// Проверяем: есть ли у поста другие активные соцсети?
					bool hasOtherActiveNetworks = post.NetworkStates
						.Any(ns => !(ns.NetworkType == (int)networkType && ns.BotId == botId) && ns.Status != (int)SocialStatus.None);

					// Если других сетей нет — удаляем пост целиком и чистим файлы в Google Drive
					if (!hasOtherActiveNetworks)
					{
						if (uploader != null && post.Media != null)
						{
							foreach (var media in post.Media)
							{
								await SafeDeleteDriveFileAsync(db, uploader, media.GoogleDriveFileId, post.Id, logger);
								if (!string.IsNullOrEmpty(media.ThumbnailDriveFileId))
								{
									await SafeDeleteDriveFileAsync(db, uploader, media.ThumbnailDriveFileId, post.Id, logger);
								}
							}
						}

						db.Posts.Remove(post);
					}
				}

				await db.SaveChangesAsync();
				logger?.LogInformation("✅ [Cleanup] Посты сети {Net} (BotId: {BotId}) успешно обработаны.", networkType, botId);
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "❌ [Cleanup] Ошибка при очистке публикаций для сети {Net} (BotId: {BotId})", networkType, botId);
			}
		}

		private static async Task SafeDeleteDriveFileAsync(AppDbContext db, IGoogleDriveUploader uploader, string? driveFileId, Guid excludingPostId, ILogger? logger)
		{
			if (string.IsNullOrEmpty(driveFileId)) return;

			try
			{
				bool isStillUsed = await db.PostMedia
					.AsNoTracking()
					.AnyAsync(m => m.GoogleDriveFileId == driveFileId && m.PostId != excludingPostId);

				if (!isStillUsed)
				{
					await uploader.DeleteFileByIdAsync(driveFileId);
					logger?.LogInformation("[Storage] Файл {DriveId} удален из Google Drive.", driveFileId);
				}
			}
			catch (Exception ex)
			{
				logger?.LogWarning(ex, "[Storage] Не удалось удалить файл {DriveId} из Google Drive", driveFileId);
			}
		}
	}
}