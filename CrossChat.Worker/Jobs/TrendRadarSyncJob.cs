using CrossChat.Data;
using CrossChat.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class TrendRadarSyncJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<TrendRadarSyncJob> _logger;

		public TrendRadarSyncJob(IServiceScopeFactory scopeFactory, ILogger<TrendRadarSyncJob> logger)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			_logger.LogInformation("[TrendRadarJob] Запуск плановой проверки трендовых хештегов...");

			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
			var radarService = scope.ServiceProvider.GetRequiredService<TrendRadarService>();

			// БЕРЕЖЕМ ЛИМИТ META (30 тегов в 7 дней):
			// Обновляем тег только если прошло более 3 дней с прошлой проверки (или еще ни разу не обновлялся)
			var thresholdDate = DateTime.UtcNow.AddDays(-3);

			var tagsToSync = await db.TrackedHashtags
				.Where(t => t.IsAutoSync && (t.LastSyncedAt == null || t.LastSyncedAt <= thresholdDate))
				.Take(5) // За один проход берем не более 5 тегов для плавности нагрузки
				.ToListAsync();

			if (!tagsToSync.Any())
			{
				_logger.LogInformation("[TrendRadarJob] Нет тегов, требующих планового обновления.");
				return;
			}

			_logger.LogInformation("[TrendRadarJob] Найдено {Count} тегов для обновления.", tagsToSync.Count);

			foreach (var tag in tagsToSync)
			{
				try
				{
					_logger.LogInformation("[TrendRadarJob] Авто-синхронизация постов для #{Tag} (User: {UserId})...", tag.Tag, tag.UserId);
					int count = await radarService.SyncHashtagPostsAsync(tag.Id, tag.UserId);
					_logger.LogInformation("[TrendRadarJob] #{Tag} обновлен: {Count} постов.", tag.Tag, count);

					// Пауза 2 сек между тегами для бережного отношения к API
					await Task.Delay(2000);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[TrendRadarJob] Ошибка авто-синхронизации тега #{Tag}", tag.Tag);
				}
			}
		}
	}
}