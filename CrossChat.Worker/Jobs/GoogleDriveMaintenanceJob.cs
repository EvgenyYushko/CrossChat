using System.Diagnostics;
using CrossChat.Data;
using CrossChat.Infrastructure.Constants;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Interfaces.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using File = Google.Apis.Drive.v3.Data.File;
using static CrossChat.Infrastructure.Constants.EnvConstants;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class GoogleDriveMaintenanceJob : IJob
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly IGoogleDriveUploader _driveUploader;
		private readonly ITelegramService _telegramService;
		private readonly ILogger<GoogleDriveMaintenanceJob> _logger;
				
		public GoogleDriveMaintenanceJob(
			IServiceScopeFactory scopeFactory,
			IGoogleDriveUploader driveUploader,
			ITelegramService telegramService,
			ILogger<GoogleDriveMaintenanceJob> logger)
		{
			_scopeFactory = scopeFactory;
			_driveUploader = driveUploader;
			_telegramService = telegramService;
			_logger = logger;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var stopwatch = Stopwatch.StartNew();
			_logger.LogInformation("🧹 [Drive GC] Запуск планового обслуживания Google Диска...");

			try
			{
				// 1. Получаем список всех существующих файлов через твой метод
				IList<File> driveFiles = await _driveUploader.GetAllFilesInFolderAsync(GOOGLE_POSTS_FOLDER_ID);
				_logger.LogInformation("📂 [Drive GC] Всего файлов на Google Диске: {Count}", driveFiles.Count);

				if (!driveFiles.Any()) return;

				// 2. Достаем из базы данных ВСЕ активные ID файлов (оригиналы + обложки)
				HashSet<string> activeDbFileIds;
				using (var scope = _scopeFactory.CreateScope())
				{
					var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

					var originalIds = await db.PostMedia
						.AsNoTracking()
						.Select(m => m.GoogleDriveFileId)
						.ToListAsync();

					var thumbnailIds = await db.PostMedia
						.AsNoTracking()
						.Where(m => m.ThumbnailDriveFileId != null)
						.Select(m => m.ThumbnailDriveFileId!)
						.ToListAsync();

					activeDbFileIds = originalIds.Concat(thumbnailIds).Distinct().ToHashSet();
				}

				_logger.LogInformation("💾 [Drive GC] Активных файлов в базе данных: {Count}", activeDbFileIds.Count);

				// 3. БУФЕР БЕЗОПАСНОСТИ: удаляем только файлы старше 24 часов
				// (чтобы не задеть медиа, которые прямо сейчас загружаются в открытом браузере)
				var safetyThresholdUtc = DateTime.UtcNow.AddHours(-24);

				var orphanedFiles = new List<File>();
				foreach (var file in driveFiles)
				{
					if (string.IsNullOrEmpty(file.Id) || activeDbFileIds.Contains(file.Id))
						continue;

					// Достаем дату создания файла в UTC
					DateTime createdTimeUtc = file.CreatedTimeDateTimeOffset?.UtcDateTime 
					                          ?? file.CreatedTime?.ToUniversalTime() 
					                          ?? DateTime.UtcNow;

					if (createdTimeUtc < safetyThresholdUtc)
					{
						orphanedFiles.Add(file);
					}
				}

				_logger.LogInformation("🗑 [Drive GC] Найдено файлов-сирот для удаления: {Count}", orphanedFiles.Count);

				int deletedCount = 0;
				long freedBytes = 0;
				var errorsList = new List<string>();

				// 4. Поочередно удаляем мусорные файлы
				foreach (var file in orphanedFiles)
				{
					try
					{
						await _driveUploader.DeleteFileByIdAsync(file.Id);
						deletedCount++;
						freedBytes += file.Size ?? 0;
						_logger.LogInformation("🗑 Удален сиротский файл: {Name} (ID: {Id}, {Size} байт)", file.Name, file.Id, file.Size ?? 0);
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Не удалось удалить файл {Id} из Google Drive", file.Id);
						errorsList.Add(file.Name ?? file.Id);
					}
				}

				stopwatch.Stop();
				double executionSeconds = stopwatch.Elapsed.TotalSeconds;
				string freedSizeFormatted = FormatBytes(freedBytes);

				_logger.LogInformation("🏁 [Drive GC] Очистка завершена. Удалено: {Count} шт., Освобождено: {Size}, Время: {Sec:F1}с",
					deletedCount, freedSizeFormatted, executionSeconds);

				// 5. ОТПРАВЛЯЕМ КРАСИВЫЙ ОТЧЕТ АДМИНУ В TELEGRAM
				string reportMessage;

				if (deletedCount > 0)
				{
					reportMessage =
						$"🧹 <b>ОБСЛУЖИВАНИЕ GOOGLE DRIVE ЗАВЕРШЕНО</b>\n\n" +
						$"📂 <b>Файлов на Диске:</b> {driveFiles.Count} шт.\n" +
						$"💾 <b>Активных файлов в БД:</b> {activeDbFileIds.Count} шт.\n" +
						$"🗑 <b>Удалено мусорных файлов:</b> {deletedCount} шт.\n" +
						$"📦 <b>Освобождено места:</b> <code>{freedSizeFormatted}</code>\n" +
						$"⏱ <b>Время выполнения:</b> {executionSeconds:F1} сек\n" +
						$"📅 <b>Дата:</b> {DateTime.UtcNow:dd.MM.yyyy HH:mm} UTC";

					if (errorsList.Any())
					{
						reportMessage += $"\n⚠️ <i>Ошибок удаления: {errorsList.Count}</i>";
					}
				}
				else
				{
					reportMessage =
						$"🧹 <b>Google Drive: Всё чисто!</b> ✨\n\n" +
						$"Файлов в облаке: {driveFiles.Count} шт.\n" +
						$"Мусорных файлов не обнаружено. Все файлы привязаны к активным публикациям.\n" +
						$"⏱ Проверка заняла: {executionSeconds:F1} сек.";
				}

				await _telegramService.SendMessageToAdmin(reportMessage);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [Drive GC] Критическая ошибка при обслуживании Google Диска");

				try
				{
					await _telegramService.SendMessageToAdmin(
						$"⚠️ <b>Ошибка обслуживания Google Drive!</b>\n\nТекст ошибки: <code>{ex.Message}</code>");
				}
				catch { }
			}
		}

		private static string FormatBytes(long bytes)
		{
			if (bytes == 0) return "0 Б";
			string[] sizes = { "Б", "КБ", "МБ", "ГБ" };
			int order = 0;
			double len = bytes;
			while (len >= 1024 && order < sizes.Length - 1)
			{
				order++;
				len /= 1024;
			}
			return $"{len:F1} {sizes[order]}";
		}
	}
}