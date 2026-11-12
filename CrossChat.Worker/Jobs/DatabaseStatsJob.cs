using System.Data;
using System.Diagnostics;
using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class DatabaseStatsJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ITelegramService _telegramService;
		private readonly ILogger<DatabaseStatsJob> _logger;
		private readonly IHostEnvironment _env;
		private readonly IConfiguration _config;

		// Лимит диска базы (например, на Render Free выделяется 1024 МБ = 1 ГБ)
		private const long DefaultQuotaBytes = 1024L * 1024 * 1024; // 1 ГБ

		public DatabaseStatsJob(
			IServiceScopeFactory scopeFactory,
			ITelegramService telegramService,
			ILogger<DatabaseStatsJob> logger,
			IHostEnvironment env,
			IConfiguration config)
		{
			_scopeFactory = scopeFactory;
			_telegramService = telegramService;
			_logger = logger;
			_env = env;
			_config = config;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			var stopwatch = Stopwatch.StartNew();
			_logger.LogInformation("📊 [DB Stats] Старт сбора статистики базы данных...");

			try
			{
				long totalDbBytes = 0;
				var tableStatsList = new List<TableStatItem>();

				using (var scope = _scopeFactory.CreateScope())
				{
					var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
					var connection = db.Database.GetDbConnection();

					if (connection.State != ConnectionState.Open)
					{
						await connection.OpenAsync();
					}

					// 1. ЗАПРОС: Общий вес всей базы данных
					using (var cmdSize = connection.CreateCommand())
					{
						cmdSize.CommandText = "SELECT pg_database_size(current_database());";
						var result = await cmdSize.ExecuteScalarAsync();
						totalDbBytes = Convert.ToInt64(result);
					}

					// 2. ЗАПРОС: Детальная статистика по каждой таблице (данные, индексы, строки)
					using (var cmdTables = connection.CreateCommand())
					{
						cmdTables.CommandText = @"
							SELECT
								c.relname AS TableName,
								COALESCE(s.n_live_tup, 0) AS RowCount,
								pg_total_relation_size(c.oid) AS TotalBytes,
								pg_relation_size(c.oid) AS TableBytes,
								pg_indexes_size(c.oid) AS IndexBytes
							FROM pg_class c
							JOIN pg_namespace n ON n.oid = c.relnamespace
							LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
							WHERE n.nspname = 'public'
							  AND c.relkind = 'r'
							ORDER BY pg_total_relation_size(c.oid) DESC;";

						using var reader = await cmdTables.ExecuteReaderAsync();
						while (await reader.ReadAsync())
						{
							tableStatsList.Add(new TableStatItem
							{
								TableName = reader.GetString(0),
								RowCount = reader.GetInt64(1),
								TotalBytes = reader.GetInt64(2),
								TableBytes = reader.GetInt64(3),
								IndexBytes = reader.GetInt64(4)
							});
						}
					}
				}

				stopwatch.Stop();
				double executionSeconds = stopwatch.Elapsed.TotalSeconds;

				// Расчёт лимитов и процентов (квоту можно задать в конфиге или 1 ГБ по дефолту)
				long quotaBytes = _config.GetValue<long?>("Database:QuotaBytes") ?? DefaultQuotaBytes;
				double usedPercent = quotaBytes > 0 ? ((double)totalDbBytes / quotaBytes) * 100.0 : 0;
				long freeBytes = Math.Max(0, quotaBytes - totalDbBytes);
				double freePercent = Math.Max(0, 100.0 - usedPercent);

				// Формируем красивое сообщение для Telegram
				var reportMessage =
					$"📊 <b>ОТЧЁТ О СОСТОЯНИИ БАЗЫ ДАННЫХ</b> 🐘\n\n" +
					$"💾 <b>Занято:</b> <code>{FormatBytes(totalDbBytes)}</code> из <code>{FormatBytes(quotaBytes)}</code> ({usedPercent:F1}%)\n" +
					$"📈 <b>Свободно:</b> <code>{FormatBytes(freeBytes)}</code> ({freePercent:F1}%)\n" +
					$"🗂 <b>Всего таблиц:</b> {tableStatsList.Count} шт.\n\n" +
					$"📋 <b>Топ таблиц по весу:</b>\n";

				// Берем топ-10 самых тяжелых таблиц
				int rank = 1;
				foreach (var t in tableStatsList.Take(10))
				{
					double tablePercentOfDb = totalDbBytes > 0 ? ((double)t.TotalBytes / totalDbBytes) * 100.0 : 0;
					reportMessage +=
						$"{rank++}. <b>{t.TableName}</b> — <code>{FormatBytes(t.TotalBytes)}</code> ({tablePercentOfDb:F1}%)\n" +
						$"   └ <i>Строк: {t.RowCount:N0} | Данные: {FormatBytes(t.TableBytes)} | Индексы: {FormatBytes(t.IndexBytes)}</i>\n";
				}

				reportMessage +=
					$"\n⏱ <b>Время проверки:</b> {executionSeconds:F2} сек\n" +
					$"📅 <b>Дата:</b> {DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC";

				_logger.LogInformation("✅ [DB Stats] Статистика успешно собрана ({Size}). Отправка в Telegram...", FormatBytes(totalDbBytes));
				await _telegramService.SendMessageToAdmin(reportMessage);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [DB Stats] Ошибка при сборе статистики базы данных");
				try
				{
					await _telegramService.SendMessageToAdmin(
						$"⚠️ <b>Ошибка сбора статистики БД!</b>\n\n<code>{ex.Message}</code>");
				}
				catch { }
			}
		}

		private static string FormatBytes(long bytes)
		{
			if (bytes == 0) return "0 Б";
			string[] sizes = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
			int order = 0;
			double len = bytes;
			while (len >= 1024 && order < sizes.Length - 1)
			{
				order++;
				len /= 1024;
			}
			return $"{len:F1} {sizes[order]}";
		}

		private class TableStatItem
		{
			public string TableName { get; set; } = string.Empty;
			public long RowCount { get; set; }
			public long TotalBytes { get; set; }
			public long TableBytes { get; set; }
			public long IndexBytes { get; set; }
		}
	}
}