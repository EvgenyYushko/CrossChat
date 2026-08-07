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

			_logger.LogInformation($"Стард джобы PostPublishingJob. Ищем посты меньше даты  {now}");

			// 1. В первом коротком скоупе достаем только ID всех состояний, готовых к публикации
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                pendingStateIds = await db.NetworkStates
                    .Where(ns => ns.Status == (int)SocialStatus.Pending && ns.Post.ShowDate <= now)
                   //.Where(ns => ns.NetworkType == 2)
                    .Select(ns => ns.Id) // Берем только простые числа ID
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
                    .ThenInclude(p => p.Images)
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
