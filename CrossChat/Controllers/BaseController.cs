using CrossChat.Data;
using CrossChat.Helpers;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	public abstract class BaseController : Controller
	{
		protected int? GetActiveProfileId()
		{
			return HttpContext.Session.GetInt32("ActiveProfileId");
		}

		protected void SetActiveProfileId(int profileId)
		{
			HttpContext.Session.SetInt32("ActiveProfileId", profileId);
		}

		/// <summary>
		/// Безопасное получение ID активного профиля с фоллбеком на дефолтный профиль пользователя из БД
		/// </summary>
		protected async Task<int> GetActiveProfileIdSafeAsync(AppDbContext db, int userId)
		{
			var profileId = GetActiveProfileId();
			if (profileId.HasValue && profileId.Value > 0)
			{
				return profileId.Value;
			}

			// Если сессионная кука слетела — берем первый профиль пользователя из базы
			var defaultProfile = await db.Profile.FirstOrDefaultAsync(p => p.UserId == userId);
			return defaultProfile?.Id ?? 0;
		}

		/// <summary>
		/// Считает количество публикаций в планировщике, привязанных к конкретному боту/каналу
		/// </summary>
		protected async Task<int> GetLinkedPostsCountAsync(NetworkType networkType, int botId)
		{
			var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
			return await db.NetworkStates
				.CountAsync(ns => ns.NetworkType == (int)networkType && ns.BotId == botId);
		}

		/// <summary>
		/// УМНАЯ ОЧИСТКА ПУБЛИКАЦИЙ И ФАЙЛОВ В ОБЛАКЕ ПРИ ОТКЛЮЧЕНИИ АККАУНТА:
		/// 1. Одиночные посты этого бота удаляются полностью (с очисткой Google Drive).
		/// 2. Мультипосты (напр. Instagram + Telegram) просто отвязываются от удаляемого бота, а в других сетях остаются жить!
		/// </summary>
		protected async Task CleanupLinkedPostsAsync(NetworkType networkType, int botId)
		{
			var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
			var uploader = HttpContext.RequestServices.GetService<IGoogleDriveUploader>();
			var logger = HttpContext.RequestServices.GetService<ILogger<BaseController>>();

			await PostCleanupHelper.CleanupLinkedPostsAsync(db, uploader, networkType, botId, logger);
		}

		/// <summary>
		/// ОЧИСТКА ДИАЛОГОВ И СООБЩЕНИЙ ИНБОКСА ПРИ ОТКЛЮЧЕНИИ АККАУНТА:
		/// Удаляет все диалоги и связанные с ними сообщения, предотвращая появление "зомби-чатов".
		/// </summary>
		protected async Task CleanupLinkedInboxAsync(NetworkType networkType, int botId)
		{
			var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
			var logger = HttpContext.RequestServices.GetService<ILogger<BaseController>>();

			try
			{
				int netTypeInt = (int)networkType;

				// 1. Находим ID всех диалогов этого бота в Инбоксе
				var conversationIds = await db.InboxConversations
					.Where(c => c.NetworkType == netTypeInt && c.BotId == botId)
					.Select(c => c.Id)
					.ToListAsync();

				if (conversationIds.Any())
				{
					// 2. Удаляем все сообщения этих диалогов (быстрый массовый DELETE в БД)
					await db.InboxMessages
						.Where(m => conversationIds.Contains(m.ConversationId))
						.ExecuteDeleteAsync();

					// 3. Удаляем сами диалоги
					await db.InboxConversations
						.Where(c => conversationIds.Contains(c.Id))
						.ExecuteDeleteAsync();

					logger?.LogInformation("🧹 [Inbox Cleanup] Успешно удалено диалогов: {Count} для {Network} (BotId: {BotId})",
						conversationIds.Count, networkType, botId);
				}
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Ошибка при очистке диалогов Инбокса для {Network} (BotId: {BotId})", networkType, botId);
			}
		}
	}
}