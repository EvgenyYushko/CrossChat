using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Services
{
	public class BlueSkyTokenManager : IBlueSkyTokenManager
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly IBlueSkyService _bskyService;
		private readonly IDatabase _redis;
		private readonly ILogger<BlueSkyTokenManager> _logger;

		public BlueSkyTokenManager(
			IServiceScopeFactory scopeFactory,
			IBlueSkyService bskyService,
			IConnectionMultiplexer redis,
			ILogger<BlueSkyTokenManager> logger)
		{
			_scopeFactory = scopeFactory;
			_bskyService = bskyService;
			_redis = redis.GetDatabase();
			_logger = logger;
		}

		public async Task<BlueSkyModel?> GetValidTokenAsync(int botDbId)
		{
			var lockKey = $"lock:bsky_token_refresh:{botDbId}";
			var lockTokenValue = Guid.NewGuid().ToString("N");

			// 1. БЫСТРАЯ ПРОВЕРКА (без взятия блокировок, если токен еще свежий)
			using (var scope = _scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
				var bot = await db.BlueSkySettings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botDbId);
				if (bot == null) return null;

				// Если до истечения токена больше 10 минут — сразу отдаем его
				if (bot.TokenExpiresAt.HasValue && bot.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(10))
				{
					return MapToModel(bot);
				}
			}

			// 2. ЗАХВАТ РАСПРЕДЕЛЕННОГО ЛОКА (ждем до 15 секунд освобождения)
			var startTime = DateTime.UtcNow;
			bool lockAcquired = false;

			while (!lockAcquired && (DateTime.UtcNow - startTime).TotalSeconds < 15)
			{
				lockAcquired = await _redis.StringSetAsync(lockKey, lockTokenValue, TimeSpan.FromSeconds(30), When.NotExists);
				if (!lockAcquired)
				{
					await Task.Delay(500); // Другой поток прямо сейчас обновляет токен — ждем
				}
			}

			if (!lockAcquired)
			{
				_logger.LogWarning("[BlueSky Lock] Не удалось захватить лок обновления токена для бота {BotId} за 15 сек.", botDbId);
				return null;
			}

			try
			{
				// 3. ВНУТРИ ЛОКА: Читаем САМЫЕ АКТУАЛЬНЫЕ данные из БД через новый скоуп
				using var scope = _scopeFactory.CreateScope();
				var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

				var bot = await db.BlueSkySettings.FirstOrDefaultAsync(b => b.Id == botDbId);
				if (bot == null || string.IsNullOrEmpty(bot.RefreshToken) || string.IsNullOrEmpty(bot.PrivateKeyJson))
				{
					_logger.LogError("[BlueSky Token] У бота {BotId} отсутствуют RefreshToken или PrivateKey", botDbId);
					return null;
				}

				// DOUBLE CHECK: пока мы ждали лок, предыдущий поток мог УЖЕ обновить токен!
				if (bot.TokenExpiresAt.HasValue && bot.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(10))
				{
					_logger.LogInformation("✅ [BlueSky Token] Токен для @{Handle} уже был обновлен параллельным потоком.", bot.Handle);
					return MapToModel(bot);
				}

				_logger.LogInformation("🔄 [BlueSky Token] Обновление токена через API BlueSky для @{Handle}...", bot.Handle);

				// 4. ДЕЛАЕМ СТРОГО ОДИН ЗАПРОС К BLUESKY API
				var refreshResult = await _bskyService.RefreshTokenAsync(bot.RefreshToken, bot.PrivateKeyJson);

				if (refreshResult == null)
				{
					_logger.LogError("❌ [BlueSky Token] Сервер BlueSky отклонил RefreshToken для @{Handle}", bot.Handle);
					return null;
				}

				// 5. МОМЕНТАЛЬНО СОХРАНЯЕМ В БД НОВЫЙ ТОКЕН
				bot.AccessToken = refreshResult.Value.AccessToken;
				bot.RefreshToken = refreshResult.Value.RefreshToken;
				// Время истечения строго в UTC
				bot.TokenExpiresAt = DateTime.UtcNow.AddSeconds(refreshResult.Value.ExpiresIn);

				await db.SaveChangesAsync();

				_logger.LogInformation("🎉 [BlueSky Token] Токен для @{Handle} успешно обновлен и сохранен в БД! Истекает: {Exp:yyyy-MM-dd HH:mm:ss} UTC",
					bot.Handle, bot.TokenExpiresAt);

				return MapToModel(bot);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [BlueSky Token] Критическая ошибка при обновлении токена бота {BotId}", botDbId);
				return null;
			}
			finally
			{
				// 6. ОСВОБОЖДАЕМ ЛОК
				var currentLock = await _redis.StringGetAsync(lockKey);
				if (currentLock == lockTokenValue)
				{
					await _redis.KeyDeleteAsync(lockKey);
				}
			}
		}

		private static BlueSkyModel MapToModel(BlueSkySettings bot)
		{
			return new BlueSkyModel
			{
				AccessToken = bot.AccessToken!,
				RefreshToken = bot.RefreshToken,
				Handle = bot.Handle,
				PrivateKeyJson = bot.PrivateKeyJson!,
				TokenExpiresAt = bot.TokenExpiresAt,
				Did = bot.Did!,
				PdsUrl = bot.PdsUrl!,
				SystemPrompt = bot.SystemPrompt
			};
		}
	}
}
