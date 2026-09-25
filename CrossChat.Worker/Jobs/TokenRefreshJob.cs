using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using StackExchange.Redis;
using static CrossChat.Integrations.Helpers.HttpHelper;
using static CrossChat.Worker.Helpers.TimeZoneHelper;

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
	private readonly IHostEnvironment _env;
	private readonly ILogger<TokenRefreshJob> _logger;
	private readonly IDatabase _redis;
	private readonly SocialMediaSettings _settings;

	public TokenRefreshJob(
		AppDbContext db,
		IOptions<SocialMediaSettings> options,
		ILogger<TokenRefreshJob> logger,
		IConnectionMultiplexer redis,
		IInstagramService instagramService,
		IThreadsService threadsService,
		IBlueSkyService blueSkyService,
		IXService xService,
		IFaceBookService faceBookService,
		IInstagramConsole instagramConsole,
		IFaceBookConsole faceBookConsole,
		IThreadsConsole threadsConsole,
		IXConsole xConsole,
		IEmailService emailService,
		IHostEnvironment env
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
		_env = env;
		_logger = logger;
		_redis = redis.GetDatabase();
		_settings = options.Value;
	}

	public async Task Execute(IJobExecutionContext context)
	{
		if (_env.IsDevelopment())
		{
			return;
		}

		_logger.LogInformation("🔄 [TokenRefreshJob] Начало комплексной проверки токенов...");
		var thresholdDate = DateTimeNow.AddDays(10);

		// --- БЛОК 1: INSTAGRAM ---
		await RefreshInstagramTokens(thresholdDate);

		// --- БЛОК 2: THREADS ---
		await RefreshThreadsTokens(thresholdDate);

		// --- БЛОК 3: BLUESKY ---
		await RefreshBlueSkyData();

		// --- БЛОК 4: X (TWITTER) ---
		await RefreshXTokens(DateTimeNow.AddHours(1));

		// --- БЛОК 5: FACEBOOK ---
		await RefreshFaceBookData();

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
					settings.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);
					await _instagramConsole.Log($"✅ Instagram токен обновлен для User {settings.UserId}", settings.UserId, settings.Id);
					
					var userInfo = await _instagramService.GetMeInfo(result.Value.NewToken);
					string? base64Icon = null;
					if (!string.IsNullOrEmpty(userInfo.profilePicUrl))
					{
						base64Icon = await DownloadImageAsBase64ForHtml(userInfo.profilePicUrl);
					}
					settings.ProfilePictureUrl = base64Icon;

					// Фиксируем сразу в базе!
					await _db.SaveChangesAsync();
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
						base64Icon = await DownloadImageAsBase64ForHtml(userInfo.ProfilePicUrl);
					}
					settings.ProfilePictureUrl = base64Icon;
					settings.PageName = userInfo.Name;

					await _db.SaveChangesAsync();
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
					settings.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);
					await _threadsConsole.Log($"Токен обновлен для {settings.Username} UserId={settings.UserId}", settings.UserId, settings.Id);

					var profile = await _threadsService.GetThreadsUserProfileAsync(result.Value.NewToken);
					string? base64Icon = null;
					if (!string.IsNullOrEmpty(profile.ProfilePictureUrl))
					{
						base64Icon = await DownloadImageAsBase64ForHtml(profile.ProfilePictureUrl);
					}
					settings.ProfilePictureUrl = base64Icon;

					// Фиксируем сразу в базе!
					await _db.SaveChangesAsync();
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

	private async Task RefreshBlueSkyData()
	{
		var bskyUsers = await _db.BlueSkySettings
			.Include(p => p.User)
			.Where(s => s.AccessToken != null)
			.ToListAsync();

		if (!bskyUsers.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] BlueSky: найдено {bskyUsers.Count} аккаунтов для проверки данных.");

		foreach (var bot in bskyUsers)
		{
			var lockKey = $"lock:bsky_token_refresh:{bot.Id}";
			var lockValue = Guid.NewGuid().ToString("N");

			// Берем тот же самый Redis Lock, что и в BluesSkyAnswerJob
			bool isLockAcquired = await _redis.StringSetAsync(lockKey, lockValue, TimeSpan.FromSeconds(30), When.NotExists);

			if (!isLockAcquired)
			{
				_logger.LogInformation($"[TokenRefreshJob] BlueSky: @{bot.Handle} сейчас обрабатывается другой джобой. Пропускаем.");
				continue;
			}

			try
			{
				// Перечитываем актуальные данные из БД
				await _db.Entry(bot).ReloadAsync();

				var botModel = new BlueSkyModel
				{
					AccessToken = bot.AccessToken!,
					RefreshToken = bot.RefreshToken,
					Handle = bot.Handle,
					PrivateKeyJson = bot.PrivateKeyJson!,
					TokenExpiresAt = bot.TokenExpiresAt,
					Did = bot.Did!,
					PdsUrl = bot.PdsUrl!
				};

				// 1. Проверяем и обновляем токен через единый метод (если истекает)
				await _blueSkyService.GetValidTokenAsync(botModel);

				// Если токен был обновлен — фиксируем изменения в сущности
				if (bot.AccessToken != botModel.AccessToken)
				{
					bot.AccessToken = botModel.AccessToken;
					bot.RefreshToken = botModel.RefreshToken;
					bot.TokenExpiresAt = botModel.TokenExpiresAt;
				}

				// 2. Запрашиваем актуальные данные профиля (аватарку и никнейм)
				var profile = await _blueSkyService.GetProfileAsync(botModel);
				if (profile != null)
				{
					if (!string.IsNullOrEmpty(profile.Value.Handle))
					{
						bot.Handle = profile.Value.Handle;
					}

					if (!string.IsNullOrEmpty(profile.Value.AvatarUrl))
					{
						bot.ProfilePictureUrl = await DownloadImageAsBase64ForHtml(profile.Value.AvatarUrl);
					}
				}

				// Сохраняем изменения в БД сразу
				await _db.SaveChangesAsync();

				_logger.LogInformation($"✅ [TokenRefreshJob] Данные профиля и токен BlueSky успешно синхронизированы для @{bot.Handle}");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"❌ [TokenRefreshJob] Ошибка обновления BlueSky для @{bot.Handle}");

				if (bot.User != null)
				{
					try
					{
						await _emailService.SendErrorRefreshToken(bot.User.Email, bot.User.Name, bot.Id, bot.Handle ?? "BlueSky", "BlueSky");
					}
					catch { }
				}
			}
			finally
			{
				// Освобождаем Redis Lock
				var currentLock = await _redis.StringGetAsync(lockKey);
				if (currentLock == lockValue)
				{
					await _redis.KeyDeleteAsync(lockKey);
				}
			}
		}
	}

	private async Task RefreshXTokens(DateTime thresholdDate)
	{
		var xBots = await _db.XSettings
			.Include(p => p.User)
			.Where(s => s.AccessToken != null && s.TokenExpiresAt < thresholdDate)
			.ToListAsync();

		if (!xBots.Any()) return;

		_logger.LogInformation($"[TokenRefreshJob] X: найдено {xBots.Count} токенов для обновления.");

		if (string.IsNullOrEmpty(_settings.XClientId) || string.IsNullOrEmpty(_settings.XClientSecret))
		{
			_logger.LogError("❌ [X Refresh] Ошибка: _settings.XClientId или _settings.XClientSecret пустые! Проверьте appsettings.json в проекте Worker.");
			return;
		}

		foreach (var settings in xBots)
		{
			try
			{
				var result = await _xService.RefreshTokenAsync(settings.RefreshToken!, _settings.XClientId, _settings.XClientSecret);

				if (result != null)
				{
					settings.AccessToken = result.Value.AccessToken;
					settings.RefreshToken = result.Value.RefreshToken; // Сохраняем новый рефреш-токен
					settings.TokenExpiresAt = DateTimeNow.AddSeconds(result.Value.ExpiresIn);

					await _xConsole.Log($"✅ X токен обновлен для @{settings.ScreenName}", settings.UserId, settings.Id);

					try
					{
						var profile = await _xService.GetXUserProfileAsync(result.Value.AccessToken);
						if (!string.IsNullOrEmpty(profile.ProfilePictureUrl))
						{
							settings.ProfilePictureUrl = await DownloadImageAsBase64ForHtml(profile.ProfilePictureUrl);
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "[X Refresh] Не удалось обновить аватарку профиля после рефреша");
					}

					// Сохраняем немедленно в БД!
					await _db.SaveChangesAsync();
				}
				else
				{
					_logger.LogWarning($"⚠️ [X Refresh] Не удалось обновить токен для @{settings.ScreenName}");
					await _xConsole.LogWarning($"⚠️ Не удалось обновить X для @{settings.ScreenName}. Возможно, доступ отозван.", settings.UserId, settings.Id);

					try
					{
						//await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.ScreenName, "X");
					}
					catch (Exception mailEx)
					{
						_logger.LogError("❌ Не удалось отправить email-уведомление (Таймаут SMTP)" + mailEx);
						await _xConsole.LogWarning("❌ Не удалось отправить email-уведомление (Таймаут SMTP)", settings.UserId, settings.Id);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, $"❌ Ошибка X Refresh для {settings.ScreenName}");

				try
				{
					await _emailService.SendErrorRefreshToken(settings.User.Email, settings.User.Name, settings.Id, settings.ScreenName, "X");
				}
				catch (Exception mailEx)
				{
					await _xConsole.LogWarning("❌ Не удалось отправить email-уведомление (Таймаут SMTP)" + mailEx, settings.UserId, settings.Id);
				}
			}
		}
	}
}