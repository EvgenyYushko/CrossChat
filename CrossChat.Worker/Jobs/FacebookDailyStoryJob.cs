using System;
using System.Linq;
using System.Threading.Tasks;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class FacebookDailyStoryJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<FacebookDailyStoryJob> _logger;

		public FacebookDailyStoryJob(IServiceScopeFactory scopeFactory, ILogger<FacebookDailyStoryJob> logger)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
			var fbService = scope.ServiceProvider.GetRequiredService<IFaceBookService>();

			var now = DateTimeNow;
			var today = now.Date;
			var currentTimeSpan = now.TimeOfDay;

			// Ищем страницы, у которых включены сторис, есть токен и сегодня сторис еще не выходила
			var candidatePages = await db.FacebookSettings
				.Where(s => s.IsActive && 
				            s.IsDailyStoriesEnabled && 
				            !string.IsNullOrEmpty(s.PageAccessToken) &&
				            (s.LastDailyStoryDate == null || s.LastDailyStoryDate.Value.Date < today))
				.ToListAsync();

			if (!candidatePages.Any()) return;

			foreach (var page in candidatePages)
			{
				try
				{
					if (!TimeSpan.TryParse(page.DailyStoryTime, out var scheduledTime))
					{
						scheduledTime = new TimeSpan(12, 0, 0);
					}

					if (currentTimeSpan >= scheduledTime)
					{
						_logger.LogInformation("⏰ Наступило время ежедневной сторис для страницы Facebook '{Page}' ({Time}). Запуск...", 
							page.PageName, page.DailyStoryTime);

						var storyDto = new FacebookDailyStoryDto
						{
							PageId = page.PageId,
							PageAccessToken = page.PageAccessToken,
							PageName = page.PageName,
							UsedMediaIdsJson = page.UsedMediaIdsJson,
							IsStoryOverlayTextEnabled = page.IsStoryOverlayTextEnabled,
							StoryOverlayText = page.StoryOverlayText
						};

						var result = await fbService.PublishDailyStoryAsync(storyDto);

						if (result.Success)
						{
							page.UsedMediaIdsJson = result.NewUsedMediaIdsJson;
							page.LastDailyStoryDate = now;
							await db.SaveChangesAsync();

							_logger.LogInformation("✅ Ежедневная сторис Facebook успешно зафиксирована в БД для '{Page}'", page.PageName);
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка выполнения ежедневной сторис Facebook для '{Page}'", page.PageName);
				}
			}
		}
	}
}