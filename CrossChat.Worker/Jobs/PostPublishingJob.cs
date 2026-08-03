using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Worker.Facades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class PostPublishingJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly ILogger<PostPublishingJob> _logger;
		private readonly SocialPublicationFacade _publisher;

		public PostPublishingJob(AppDbContext db, ILogger<PostPublishingJob> logger, SocialPublicationFacade publisher)
		{
			_db = db;
			_logger = logger;
			_publisher = publisher;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var now = DateTimeNow;
			
			_logger.LogInformation($"Стард джобы PostPublishingJob. Ищем посты меньше даты  {now}");

			// 1. Достаем из БД все публикации, время которых наступило
			var pendingStates = await _db.NetworkStates
				.Include(ns => ns.Post)
				.ThenInclude(p => p.Images)
				.Where(ns => ns.Status == (int)SocialStatus.Pending && ns.Post.ShowDate <= now)
				.ToListAsync();

			if (!pendingStates.Any()) return;

			_logger.LogInformation($"Найдено {pendingStates.Count} публикаций для отправки.");

			// 2. Публикуем посты параллельно (максимум 5 одновременно, чтобы не спамить API)
			await Parallel.ForEachAsync(pendingStates, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (state, ct) =>
			{
				try
				{
					//_logger.LogInformation("Запуск публикации поста {PostId} в сеть {NetType}", state.PostId, state.NetworkType);

					// Меняем статус на "В процессе", чтобы избежать повторной отправки
					//state.Status = (int)SocialStatus.Processing;
					//await _db.SaveChangesAsync(ct);

					// Вызываем ваш сервис отправки поста в API конкретной соцсети (Telegram, Instagram...)
					await _publisher.PublishToSocialNetworkAsync(state);

					// Если все прошло успешно — ставим статус Опубликовано
					state.Status = (int)SocialStatus.Published;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка при публикации поста {PostId} в сеть {NetType}", state.PostId, state.NetworkType);

					// В случае ошибки ставим статус Error (чтобы повторно не пытаться бесконечно)
					state.Status = (int)SocialStatus.Error;
				}
				finally
				{
					await _db.SaveChangesAsync(ct);
				}
			});
		}
	}
}
