using System.Diagnostics;
using CrossChat.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class InboxCleanupJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly ILogger<InboxCleanupJob> _logger;
		private readonly IHostEnvironment _env;

		// Сколько последних сообщений гарантированно хранить в каждом диалоге:
		private const int KEEP_MESSAGES_COUNT = 50;

		public InboxCleanupJob(
			IServiceScopeFactory scopeFactory,
			ILogger<InboxCleanupJob> logger,
			IHostEnvironment env)
		{
			_scopeFactory = scopeFactory;
			_logger = logger;
			_env = env;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			if (_env.IsDevelopment())
			{
				return;
			}

			var sw = Stopwatch.StartNew();
			_logger.LogInformation("🧹 [Inbox GC] Запуск ночной очистки архивных сообщений чатов (храним топ-{Count})...", KEEP_MESSAGES_COUNT);

			using var scope = _scopeFactory.CreateScope();
			var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

			try
			{
				// 🚀 ВЫСОКОСКОРОСТНАЯ ОЧИСТКА В 1 ЗАПРОС ЧЕРЕЗ ОКОННУЮ ФУНКЦИЮ POSTGRESQL:
				// Оставляет ровно 50 самых новых сообщений в каждом диалоге, все остальные удаляет.
				string sql = $@"
					DELETE FROM ""InboxMessages""
					WHERE ""Id"" IN (
						SELECT ""Id"" FROM (
							SELECT ""Id"", 
								   ROW_NUMBER() OVER (PARTITION BY ""ConversationId"" ORDER BY ""CreatedAtUtc"" DESC) as rn
							FROM ""InboxMessages""
						) sub
						WHERE sub.rn > {KEEP_MESSAGES_COUNT}
					);";

				int deletedCount = await db.Database.ExecuteSqlRawAsync(sql);
				sw.Stop();

				if (deletedCount > 0)
				{
					_logger.LogInformation("✅ [Inbox GC] Очистка успешно завершена. Удалено старых сообщений: {Count} шт. Время: {Ms} мс", 
						deletedCount, sw.ElapsedMilliseconds);
				}
				else
				{
					_logger.LogInformation("👌 [Inbox GC] База чатов в идеальном порядке. Старых сообщений (> {Count}) не обнаружено. Время: {Ms} мс", 
						KEEP_MESSAGES_COUNT, sw.ElapsedMilliseconds);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [Inbox GC] Ошибка при очистке старых сообщений Инбокса");
			}
		}
	}
}