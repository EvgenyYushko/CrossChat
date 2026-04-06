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
	private readonly IThreadsService _threadsService;
	private readonly ILogger<TokenRefreshJob> _logger;

	public TokenRefreshJob(
		AppDbContext db,
		IInstagramService instagramService,
		IThreadsService threadsService,
		ILogger<TokenRefreshJob> logger)
	{
		_db = db;
		_instagramService = instagramService;
		_threadsService = threadsService;
		_logger = logger;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		_logger.LogInformation("🔄 [TokenRefreshJob] Начало комплексной проверки токенов...");
		var thresholdDate = DateTime.UtcNow.AddDays(10);

		// --- БЛОК 1: INSTAGRAM ---
		await RefreshInstagramTokens(thresholdDate);

		// --- БЛОК 2: THREADS ---
		await RefreshThreadsTokens(thresholdDate);

		// Сохраняем все изменения в БД одним махом
		await _db.SaveChangesAsync();
		_logger.LogInformation("🏁 [TokenRefreshJob] Все задачи по обновлению завершены.");
	}

	private async Task RefreshInstagramTokens(DateTime thresholdDate)
	{
		var instaUsers = await _db.InstagramSettings
			.Where(s => s.AccessToken != null && s.TokenExpiresAt != null && s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		if (!instaUsers.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] Instagram: найдено {instaUsers.Count} токенов.");

		foreach (var settings in instaUsers)
		{
			try
			{
				var result = await _instagramService.RefreshTokenAsync(settings.AccessToken!);
				if (result != null)
				{
					settings.AccessToken = result.Value.NewToken;
					settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);
					_logger.LogInformation($"✅ Instagram токен обновлен для User {settings.UserId}");
				}
			}
			catch (Exception ex) { _logger.LogError(ex, $"❌ Ошибка Instagram User {settings.UserId}"); }
		}
	}

	private async Task RefreshThreadsTokens(DateTime thresholdDate)
	{
		// Выбираем все записи Threads, которые скоро протухнут
		var threadsUsers = await _db.ThreadsSettings
			.Where(s => s.AccessToken != null && s.TokenExpiresAt != null && s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		if (!threadsUsers.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] Threads: найдено {threadsUsers.Count} токенов.");

		foreach (var settings in threadsUsers)
		{
			try
			{
				var result = await _threadsService.RefreshTokenAsync(settings.AccessToken!);
				if (result != null)
				{
					settings.AccessToken = result.Value.NewToken;
					settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);
					_logger.LogInformation($"✅ Threads токен обновлен для User {settings.UserId}");
				}
				else
				{
					_logger.LogWarning($"⚠️ Не удалось обновить Threads токен для User {settings.UserId}");
				}
			}
			catch (Exception ex) { _logger.LogError(ex, $"❌ Ошибка Threads User {settings.UserId}"); }
		}
	}
}