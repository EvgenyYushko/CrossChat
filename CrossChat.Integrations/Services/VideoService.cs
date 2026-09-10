using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services
{
	public static class VideoService
	{
		/// <summary>
		/// Мгновенно удаляет все метаданные и манифесты C2PA (метку ИИ) из MP4 файла без перекодирования (без потери качества).
		/// </summary>
		public static async Task<bool> StripAiMetadataAsync(string videoPath, ILogger? logger = null)
		{
			if (!File.Exists(videoPath)) return false;

			string directory = Path.GetDirectoryName(videoPath)!;
			string cleanFileName = $"clean_{Path.GetFileName(videoPath)}";
			string cleanPath = Path.Combine(directory, cleanFileName);

			try
			{
				// -y : перезаписывать файл назначения
				// -map_metadata -1 : удалить все глобальные и потоковые метаданные (C2PA)
				// -c copy : скопировать видео/аудио потоки без пережатия (0.1 секунды)
				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -i \"{videoPath}\" -map_metadata -1 -c copy \"{cleanPath}\"",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				using var process = new Process { StartInfo = startInfo };
				process.Start();

				// Читаем stderr, чтобы предотвратить дедлок процесса
				var errorTask = process.StandardError.ReadToEndAsync();
				await process.WaitForExitAsync();
				string errorOutput = await errorTask;

				if (process.ExitCode == 0 && File.Exists(cleanPath))
				{
					// Заменяем исходный файл очищенным
					File.Delete(videoPath);
					File.Move(cleanPath, videoPath);

					logger?.LogInformation("✅ Метаданные ИИ успешно удалены из видео: {Path}", videoPath);
					return true;
				}

				logger?.LogWarning("⚠️ FFmpeg завершился с кодом {Code}: {Error}", process.ExitCode, errorOutput);
				return false;
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "❌ Ошибка при вызове FFmpeg для очистки метаданных видео: {Path}", videoPath);
				return false; // В случае ошибки публикуем оригинальный файл как есть
			}
			finally
			{
				if (File.Exists(cleanPath))
				{
					try { File.Delete(cleanPath); } catch { }
				}
			}
		}
	}
}
