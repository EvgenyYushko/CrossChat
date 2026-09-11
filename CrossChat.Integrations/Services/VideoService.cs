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

		/// <summary>
		/// Конвертирует любой аудиофайл (mp3, wav, m4a) в эталонный формат Telegram Voice (OGG Opus 32k)
		/// </summary>
		public static async Task<byte[]> ConvertToTelegramVoiceOggAsync(byte[] inputAudioBytes)
		{
			string tempInput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_input.tmp");
			string tempOutput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_voice.ogg");

			try
			{
				await File.WriteAllBytesAsync(tempInput, inputAudioBytes);

				// -c:a libopus -b:a 32k -vbr on : параметры нативного Telegram Voice
				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -i \"{tempInput}\" -c:a libopus -b:a 32k -vbr on -vn \"{tempOutput}\"",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				using var process = new Process { StartInfo = startInfo };
				process.Start();

				var errorTask = process.StandardError.ReadToEndAsync();
				await process.WaitForExitAsync();
				string errorOutput = await errorTask;

				if (process.ExitCode == 0 && File.Exists(tempOutput))
				{
					return await File.ReadAllBytesAsync(tempOutput);
				}

				Console.WriteLine("FFmpeg не смог конвертировать аудио в Voice: {Err}", errorOutput);
				return inputAudioBytes; // В случае сбоя отдаем как есть
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при конвертации аудио в Telegram Voice через FFmpeg {ex}");
				return inputAudioBytes;
			}
			finally
			{
				if (File.Exists(tempInput)) try { File.Delete(tempInput); } catch { }
				if (File.Exists(tempOutput)) try { File.Delete(tempOutput); } catch { }
			}
		}
	}
}
