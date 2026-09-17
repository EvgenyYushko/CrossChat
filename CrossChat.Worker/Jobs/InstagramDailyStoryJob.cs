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
	public class InstagramDailyStoryJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<InstagramDailyStoryJob> _logger;

		public InstagramDailyStoryJob(IServiceScopeFactory scopeFactory, ILogger<InstagramDailyStoryJob> logger)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
			var instaService = scope.ServiceProvider.GetRequiredService<IInstagramService>();

			var now = DateTimeNow;
			var today = now.Date;
			var currentTimeSpan = now.TimeOfDay;

			// Находим ботов, у которых:
			// 1. Включен постинг сторис
			// 2. Есть AccessToken
			// 3. Сторис сегодня ЕЩЕ НЕ ВЫХОДИЛА
			var candidateBots = await db.InstagramSettings
				.Where(s => s.IsDailyStoriesEnabled &&
							!string.IsNullOrEmpty(s.AccessToken) &&
							(s.LastDailyStoryDate == null || s.LastDailyStoryDate.Value.Date < today))
				.ToListAsync();

			if (!candidateBots.Any()) return;

			foreach (var bot in candidateBots)
			{
				try
				{
					// Парсим заданное время (например "14:30")
					if (!TimeSpan.TryParse(bot.DailyStoryTime, out var scheduledTime))
					{
						scheduledTime = new TimeSpan(12, 0, 0); // дефолт 12:00
					}

					// Публикуем, если текущее время уже достигло назначенного времени
					if (currentTimeSpan >= scheduledTime)
					{
						_logger.LogInformation("⏰ Наступило время ежедневной сторис для @{User} ({Time}). Запуск...",
							bot.Username, bot.DailyStoryTime);

						var storyDto = new InstagramDailyStoryDto
						{
							AccessToken = bot.AccessToken,
							Username = bot.Username,
							UsedMediaIdsJson = bot.UsedMediaIdsJson
						};

						var result = await instaService.PublishDailyStoryAsync(storyDto);

						if (result.Success)
						{
							// Обновляем сущность базы данных
							bot.UsedMediaIdsJson = result.NewUsedMediaIdsJson;
							bot.LastDailyStoryDate = now;

							await db.SaveChangesAsync();
							_logger.LogInformation("✅ Данные сторис успешно зафиксированы в БД для @{User}", bot.Username);
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка выполнения ежедневной сторис для @{User}", bot.Username);
				}
			}
		}
	}
}