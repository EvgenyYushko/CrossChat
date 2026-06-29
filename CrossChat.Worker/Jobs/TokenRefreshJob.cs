using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using static CrossChat.Worker.Helpers.HttpHelper;

namespace CrossChat.Worker.Jobs;

[DisallowConcurrentExecution] // Чтобы джоба не запустилась второй раз, если первая еще работает
public class TokenRefreshJob : IJob
{
	private readonly AppDbContext _db;
	private readonly IInstagramService _instagramService;
	private readonly IThreadsService _threadsService;
	private readonly IBlueSkyService _blueSkyService;
	private readonly IXService _xService;
	private readonly IFaceBookService _faceBookService;
	private readonly IInstagramConsole _instagramConsole;
	private readonly IFaceBookConsole _faceBookConsole;
	private readonly IThreadsConsole _threadsConsole;
	private readonly IXConsole _xConsole;
	private readonly IEmailService _emailService;
	private readonly ILogger<TokenRefreshJob> _logger;
	private readonly SocialMediaSettings _settings;

	public TokenRefreshJob(
		AppDbContext db,
		IOptions<SocialMediaSettings> options,
		ILogger<TokenRefreshJob> logger,
		IInstagramService instagramService,
		IThreadsService threadsService,
		IBlueSkyService blueSkyService,
		IXService xService,
		IFaceBookService faceBookService,
		IInstagramConsole instagramConsole,
		IFaceBookConsole faceBookConsole,
		IThreadsConsole threadsConsole,
		IXConsole xConsole,
		IEmailService emailService
		)
	{
		_db = db;
		_instagramService = instagramService;
		_threadsService = threadsService;
		_blueSkyService = blueSkyService;
		_xService = xService;
		_faceBookService = faceBookService;
		_instagramConsole = instagramConsole;
		_faceBookConsole = faceBookConsole;
		_threadsConsole = threadsConsole;
		_xConsole = xConsole;
		_emailService = emailService;
		_logger = logger;
		_settings = options.Value;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		return;
		_logger.LogInformation("🔄 [TokenRefreshJob] Начало комплексной проверки токенов...");
		var thresholdDate = DateTime.UtcNow.AddDays(10);

		// --- БЛОК 1: INSTAGRAM ---
		await RefreshInstagramTokens(thresholdDate);

		// --- БЛОК 2: THREADS ---
		await RefreshThreadsTokens(thresholdDate);

		// --- БЛОК 3: BLUESKY ---
		//await RefreshBlueSkyTokens(DateTime.UtcNow.AddHours(1));

		await RefreshXTokens(DateTime.UtcNow.AddMinutes(20));

		await RefreshFaceBookData();

		// Сохраняем все изменения в БД одним махом
		await _db.SaveChangesAsync();
		_logger.LogInformation("🏁 [TokenRefreshJob] Все задачи по обновлению завершены.");
	}

	private async Task RefreshInstagramTokens(DateTime thresholdDate)
	{
		var instaUsers = await _db.InstagramSettings
			.Include(p => p.User)
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
					await _instagramConsole.Log($"✅ Instagram токен обновлен для User {settings.UserId}", settings.UserId, settings.Id);
					var userInfo = await _instagramService.GetMeInfo(result.Value.NewToken);
					string? base64Icon = null;
					if (!string.IsNullOrEmpty(userInfo.profilePicUrl))
					{
						base64Icon = await DownloadImageAsBase64(userInfo.profilePicUrl);
					}
					settings.ProfilePictureUrl = base64Icon;
				}
			}
			catch (Exception ex)
			{
                await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.Username, "Instagram");
				await _instagramConsole.LogError($"❌ Ошибка Instagram User {settings.UserId}, {ex}", settings.UserId, settings.Id);
			}
		}
	}

	private async Task RefreshFaceBookData()
	{
		var users = await _db.FacebookSettings
			.Include(p => p.User)
			.Where(s => s.IsActive)
			.ToListAsync();

		if (!users.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] FaceBook: найдено {users.Count} токенов.");

		foreach (var settings in users)
		{
			try
			{
				var userInfo = await _faceBookService.GetMeAsync(settings.PageAccessToken);
				if (userInfo != null)
				{
					await _faceBookConsole.Log($"✅ FaceBook данные обновлены для User {settings.UserId}", settings.UserId, settings.Id);

					string? base64Icon = null;
					if (!string.IsNullOrEmpty(userInfo.ProfilePicUrl))
					{
						base64Icon = await DownloadImageAsBase64(userInfo.ProfilePicUrl);
					}
					settings.ProfilePictureUrl = base64Icon;
					settings.PageName = userInfo.Name;
				}
			}
			catch (Exception ex)
			{
                await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.PageName, "FaceBook");
				await _faceBookConsole.LogError($"❌ Ошибка FaceBook User {settings.UserId}, {ex}", settings.UserId, settings.Id);
			}
		}
	}

	private async Task RefreshThreadsTokens(DateTime thresholdDate)
	{
		// Выбираем все записи Threads, которые скоро протухнут
		var threadsUsers = await _db.ThreadsSettings
			.Include(p => p.User)
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
					await _threadsConsole.Log($"токен обновлен для {settings.Username} UserId={settings.UserId}", settings.UserId, settings.Id);

					var profile = await _threadsService.GetThreadsUserProfileAsync(result.Value.NewToken);
					string? base64Icon = null;
					if (!string.IsNullOrEmpty(profile.ProfilePictureUrl))
					{
						base64Icon = await DownloadImageAsBase64(profile.ProfilePictureUrl);
					}
					settings.ProfilePictureUrl = base64Icon;
				}
				else
				{
					await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.Username, "Threads");
					await _threadsConsole.LogWarning($"⚠️ Не удалось обновить Threads токен для {settings.Username} User {settings.UserId}", settings.UserId, settings.Id);
				}
			}
			catch (Exception ex)
			{
				await _threadsConsole.LogError($"❌ Ошибка Threads {settings.Username} User {settings.UserId}, {ex}", settings.UserId, settings.Id);
			}
		}
	}

	private async Task RefreshBlueSkyTokens(DateTime thresholdDate)
	{
		var bskyUsers = await _db.BlueSkySettings
			.Where(s => s.AccessToken != null && s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		foreach (var bot in bskyUsers)
		{
			try
			{
				// Обновляем токен
				var result = await _blueSkyService.RefreshTokenAsync(bot.RefreshToken!, bot.PrivateKeyJson!);

				if (result != null)
				{
					bot.AccessToken = result.Value.AccessToken;
					bot.RefreshToken = result.Value.RefreshToken;
					bot.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);
					_logger.LogInformation($"✅ BlueSky токен обновлен для @{bot.Handle}");
				}
				else
				{
					_logger.LogWarning($"⚠️ Не удалось обновить BlueSky для @{bot.Handle}. Возможно, отозван.");
				}
			}
			catch (Exception ex) { _logger.LogError(ex, $"❌ Ошибка BlueSky Refresh для {bot.Handle}"); }
		}
	}

	private async Task RefreshXTokens(DateTime thresholdDate)
	{
		// Ищем токены X, которые скоро истекают
		var xBots = await _db.XSettings
			.Include(p => p.User)
			.Where(s => s.AccessToken != null && s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		if (!xBots.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] X: найдено {xBots.Count} токенов для обновления.");

		foreach (var settings in xBots)
		{
			try
			{
				var result = await _xService.RefreshTokenAsync(settings.RefreshToken!, _settings.XClientId, _settings.XClientSecret);

				if (result != null)
				{
					settings.AccessToken = result.Value.AccessToken;
					settings.RefreshToken = result.Value.RefreshToken; // ОБЯЗАТЕЛЬНО сохраняем новый рефреш-токен
					settings.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);

					await _xConsole.Log($"✅ X токен обновлен для @{settings.ScreenName}", settings.UserId, settings.Id);

					var profile = await _xService.GetXUserProfileAsync(result.Value.AccessToken);
					string? base64Icon = null;
					if (!string.IsNullOrEmpty(profile.ProfilePictureUrl))
					{
						base64Icon = await DownloadImageAsBase64(profile.ProfilePictureUrl);
					}
					settings.ProfilePictureUrl = base64Icon;
				}
				else
				{
					await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.ScreenName, "X");
					await _xConsole.LogWarning($"⚠️ Не удалось обновить X для @{settings.ScreenName}. Возможно, доступ отозван.", settings.UserId, settings.Id);
					// settings.IsActive = false; // Опционально: выключаем бота при ошибке
				}
			}
			catch (Exception ex)
			{
				await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.ScreenName, "X");
				await _xConsole.LogError($"❌ Ошибка X Refresh для {settings.ScreenName}, {ex}", settings.UserId, settings.Id);
			}
		}
	}
}