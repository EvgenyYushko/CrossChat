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

		/// <summary>
		/// Автоматически обрезает любое видео в квадрат 1:1 (640x640, YUV420P) для нативного видео-кружочка Telegram
		/// </summary>
		public static async Task<byte[]> ConvertToTelegramVideoNoteAsync(byte[] inputVideoBytes)
		{
			string tempInput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_circle_in.mp4");
			string tempOutput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_circle_out.mp4");

			try
			{
				await File.WriteAllBytesAsync(tempInput, inputVideoBytes);

				// 1. crop='min(iw,ih)':'min(iw,ih)':(iw-ow)/2:(ih-oh)*0.3 : вырезаем квадрат с легким смещением вверх к лицу (30%)
				// 2. scale=640:640 : эталонный размер кружочка Telegram
				// 3. -t 60 : обрезаем максимум до 60 секунд (жесткий лимит Telegram Video Note)
				// 4. -pix_fmt yuv420p : обязательный цветовой профиль для iOS и Android
				var filter = "crop='min(iw,ih)':'min(iw,ih)':(iw-ow)/2:(ih-oh)*0.3,scale=640:640";

				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -i \"{tempInput}\" -vf \"{filter}\" -t 60 -c:v libx264 -profile:v baseline -level 3.0 -pix_fmt yuv420p -preset veryfast -b:v 1500k -c:a aac -b:a 128k -ar 44100 -movflags +faststart \"{tempOutput}\"",
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

				Console.WriteLine("FFmpeg не смог создать VideoNote: {Err}", errorOutput);
				return inputVideoBytes;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при конвертации видео в VideoNote через FFmpeg {ex}");
				return inputVideoBytes;
			}
			finally
			{
				if (File.Exists(tempInput)) try { File.Delete(tempInput); } catch { }
				if (File.Exists(tempOutput)) try { File.Delete(tempOutput); } catch { }
			}
		}

		/// <summary>
		/// Извлекает первый кадр из видеофайла в виде компактного JPEG-изображения (обложки)
		/// </summary>
		public static async Task<string?> GenerateVideoThumbnailAsync(string videoPath)
		{
			if (!File.Exists(videoPath)) return null;

			string directory = Path.GetDirectoryName(videoPath)!;
			string thumbPath = Path.Combine(directory, $"{Guid.NewGuid()}_thumb.jpg");

			try
			{
				// -ss 00:00:00.500 : берем кадр на полусекунде (чтобы не брать чисто черный экран титров)
				// -vframes 1 : ровно один кадр
				// -q:v 2 : высокое качество JPEG
				// -vf "scale=480:-1" : сжимаем ширину до 480px (для превью больше не нужно)
				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -ss 00:00:00.500 -i \"{videoPath}\" -vframes 1 -q:v 2 -vf \"scale=480:-1\" \"{thumbPath}\"",
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

				if (process.ExitCode == 0 && File.Exists(thumbPath))
				{
					return thumbPath;
				}

				// Если на 0.5 сек не вышло (видео короче), пробуем самый первый кадр (0 сек)
				var fallbackInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -i \"{videoPath}\" -vframes 1 -q:v 2 -vf \"scale=480:-1\" \"{thumbPath}\"",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				using var fallbackProcess = new Process { StartInfo = fallbackInfo };
				fallbackProcess.Start();
				await fallbackProcess.WaitForExitAsync();

				if (fallbackProcess.ExitCode == 0 && File.Exists(thumbPath))
				{
					return thumbPath;
				}

				Console.WriteLine("FFmpeg не смог извлечь кадр из видео: {Err}", errorOutput);
				return null;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"{ex} Ошибка при генерации превью для видео: {videoPath}");
				return null;
			}
		}
	}
}
