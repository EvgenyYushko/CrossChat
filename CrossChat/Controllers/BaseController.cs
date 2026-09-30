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
	}
}