using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Worker.Facades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class PostPublishingJob : IJob
	{
		private readonly ILogger<PostPublishingJob> _logger;
		private readonly IServiceScopeFactory _scopeFactory;

		public PostPublishingJob(IServiceScopeFactory scopeFactory, ILogger<PostPublishingJob> logger)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var now = DateTimeNow;
			List<int> pendingStateIds;

			using (var scope = _scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

				// === ЗАЩИТА ОТ ЗАЛПОВОГО СПАМА (СЕРВЕР БЫЛ ВЫКЛЮЧЕН) ===
				// Если пост запланирован более 24 часов назад — отменяем авто-публикацию
				var maxAllowedDelay = TimeSpan.FromHours(24);
				var expiredThreshold = now - maxAllowedDelay;

				var expiredStates = await db.NetworkStates
					.Include(ns => ns.Post)
					.Where(ns => ns.Status == (int)SocialStatus.Pending && ns.Post.ShowDate < expiredThreshold)
					.ToListAsync();

				if (expiredStates.Any())
				{
					foreach (var exp in expiredStates)
					{
						exp.Status = (int)SocialStatus.Error;
						_logger.LogWarning("⚠️ Пост {PostId} просрочен (дата: {Date}). Публикация отменена во избежание спам-бана.",
							exp.PostId, exp.Post.ShowDate);
					}
					await db.SaveChangesAsync();
				}

				// Берем только АКТУАЛЬНЫЕ посты (ShowDate <= now И не старше 24 часов)
				pendingStateIds = await db.NetworkStates
					.Where(ns => ns.Status == (int)SocialStatus.Pending &&
								 ns.Post.ShowDate <= now &&
								 ns.Post.ShowDate >= expiredThreshold)
					.Select(ns => ns.Id)
					.ToListAsync();
			}

			if (!pendingStateIds.Any()) return;

			_logger.LogInformation("Найдено {Count} публикаций для отправки.", pendingStateIds.Count);

			// 2. Обрабатываем публикации параллельно. 
			// Каждый поток получает СОБСТВЕННЫЙ изолированный DbContext!
			await Parallel.ForEachAsync(pendingStateIds, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (stateId, ct) =>
			{
				// Создаем отдельный независимый скоуп для этого конкретного потока
				using var scope = _scopeFactory.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
				var publisher = scope.ServiceProvider.GetRequiredService<SocialPublicationFacade>(); // Ваш сервис публикации

				// Загружаем пост строго для текущего потока
				var state = await db.NetworkStates
					.Include(ns => ns.Post)
					.ThenInclude(p => p.Media)
					.FirstOrDefaultAsync(ns => ns.Id == stateId, ct);

				if (state == null) return;

				try
				{
					_logger.LogInformation("Запуск публикации поста {PostId} в сеть {NetType} (BotId: {BotId})",
						state.PostId, state.NetworkType, state.BotId);

					// Вызываем публикацию в соцсеть
					await publisher.PublishToSocialNetworkAsync(state);

					// Успешная публикация
					state.Status = (int)SocialStatus.Published;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка при публикации поста {PostId} в сеть {NetType}", state.PostId, state.NetworkType);
					state.Status = (int)SocialStatus.Error;
				}
				finally
				{
					await db.SaveChangesAsync(ct);
				}
			});
		}
	}
}
