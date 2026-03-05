using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs;

[DisallowConcurrentExecution] // Чтобы джоба не запустилась второй раз, если первая еще работает
public class TokenRefreshJob : IJob
{
	private readonly AppDbContext _db;
	private readonly IInstagramService _instagramService;
	private readonly ILogger<TokenRefreshJob> _logger;

	public TokenRefreshJob(
		AppDbContext db,
		IInstagramService instagramService,
		ILogger<TokenRefreshJob> logger)
	{
		_db = db;
		_instagramService = instagramService;
		_logger = logger;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		_logger.LogInformation("🔄 [TokenRefreshJob] Начало проверки токенов...");

		// 1. Ищем токены, которые истекают в ближайшие 10 дней
		// Исключаем те, у которых токена нет или дата не стоит
		var thresholdDate = DateTime.UtcNow.AddDays(10);

		var usersToRefresh = await _db.InstagramSettings
			.Where(s => s.AccessToken != null &&
						s.TokenExpiresAt != null &&
						s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		if (!usersToRefresh.Any())
		{
			_logger.LogInformation("✅ [TokenRefreshJob] Нет токенов для обновления.");
			return;
		}

		_logger.LogInformation($"[TokenRefreshJob] Найдено {usersToRefresh.Count} токенов для обновления.");

		// 2. Обновляем каждый токен
		foreach (var settings in usersToRefresh)
		{
			try
			{
				var result = await _instagramService.RefreshTokenAsync(settings.AccessToken!);

				if (result != null)
				{
					settings.AccessToken = result.Value.NewToken;
					// Обновляем дату (UTC сейчас + сколько секунд дал Инстаграм)
					settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);

					_logger.LogInformation($"[TokenRefreshJob] Успешно обновлен токен для User {settings.UserId}");
				}
				else
				{
					// Если токен протух окончательно или отозван - можно пометить бота как неактивного
					// settings.IsActive = false; 
					_logger.LogWarning($"[TokenRefreshJob] Не удалось обновить токен для User {settings.UserId}. Возможно, он отозван.");
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"[TokenRefreshJob] Ошибка при обработке User {settings.UserId}");
			}
		}

		// 3. Сохраняем изменения в БД одним махом
		await _db.SaveChangesAsync();
		_logger.LogInformation("🏁 [TokenRefreshJob] Завершено.");
	}
}