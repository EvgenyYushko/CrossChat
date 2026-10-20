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
		/// Мгновенно удаляет все метаданные и манифесты C2PA (метку ИИ) из массива байтов видео без пережатия (0.1 сек).
		/// </summary>
		public static async Task<byte[]> StripAiMetadataAsync(byte[] videoBytes, ILogger? logger = null)
		{
			if (videoBytes == null || videoBytes.Length == 0) return videoBytes;

			string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_yt_clean.mp4");

			try
			{
				// Записываем во временный файл
				await File.WriteAllBytesAsync(tempFile, videoBytes);

				// Вызываем наш проверенный метод очистки FFmpeg (-map_metadata -1 -c copy)
				bool success = await StripAiMetadataAsync(tempFile, logger);

				if (success && File.Exists(tempFile))
				{
					return await File.ReadAllBytesAsync(tempFile);
				}

				return videoBytes;
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Ошибка при очистке метаданных ИИ из массива байтов видео");
				return videoBytes;
			}
			finally
			{
				if (File.Exists(tempFile))
				{
					try { File.Delete(tempFile); } catch { }
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

		/// <summary>
		/// Накладывает прозрачную PNG-плашку поверх видеофайла через FFmpeg со случайной вертикальной позицией
		/// </summary>
		public static async Task<bool> OverlayBadgeOnVideoAsync(string videoPath, byte[] badgePngBytes, float yRatio, ILogger? logger = null)
		{
			if (!File.Exists(videoPath) || badgePngBytes == null || badgePngBytes.Length == 0)
				return false;

			string directory = Path.GetDirectoryName(videoPath)!;
			string tempBadge = Path.Combine(directory, $"{Guid.NewGuid()}_badge.png");
			string tempOutput = Path.Combine(directory, $"{Guid.NewGuid()}_overlaid.mp4");

			try
			{
				// Сохраняем прозрачный PNG-стикер во временный файл
				await File.WriteAllBytesAsync(tempBadge, badgePngBytes);

				// Форматируем yRatio строго с точкой для FFmpeg (напр: 0.52)
				string yRatioStr = yRatio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

				// Фильтр: центрируем по горизонтали (W-w)/2 и ставим на высоту (H-h)*yRatio
				string filter = $"overlay=(W-w)/2:(H-h)*{yRatioStr}";

				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = $"-y -i \"{videoPath}\" -i \"{tempBadge}\" -filter_complex \"{filter}\" -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -c:a copy -map_metadata -1 \"{tempOutput}\"",
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
					File.Delete(videoPath);
					File.Move(tempOutput, videoPath);
					logger?.LogInformation("✅ Стикер успешно наложен на видео: {Path}", videoPath);
					return true;
				}

				logger?.LogWarning("FFmpeg не смог наложить стикер на видео: {Err}", errorOutput);
				return false;
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Ошибка при наложении стикера на видео: {Path}", videoPath);
				return false;
			}
			finally
			{
				if (File.Exists(tempBadge)) try { File.Delete(tempBadge); } catch { }
				if (File.Exists(tempOutput)) try { File.Delete(tempOutput); } catch { }
			}
		}

		/// <summary>
		/// Безопасное сжатие видео для Render (ограничение -threads 1 защищает от перезапуска сервера по памяти!)
		/// </summary>
		public static async Task<string> CompressVideoForDirectAsync(string inputPath, ILogger? logger = null)
		{
			if (!File.Exists(inputPath)) return inputPath;

			var fileInfo = new FileInfo(inputPath);
			// Сжимаем ролики крупнее 20 МБ
			if (fileInfo.Length <= 20 * 1024 * 1024) return inputPath;

			string directory = Path.GetDirectoryName(inputPath)!;
			string outputPath = Path.Combine(directory, $"comp_{Guid.NewGuid()}.mp4");

			try
			{
				logger?.LogInformation("🎬 [FFmpeg] Безопасное сжатие видео {Size:F1} МБ для Direct...", fileInfo.Length / (1024.0 * 1024.0));

				// -threads 1 КРИТИЧЕСКИ ВАЖЕН ДЛЯ RENDER: удерживает память в пределах 80 МБ!
				// -pix_fmt yuv420p гарантирует, что цвета с iPhone (HDR/HEVC) корректно прочитаются серверами Meta
				var arguments = $"-y -i \"{inputPath}\" -threads 1 -vf \"scale='min(720,iw)':-2\" " +
								$"-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p " +
								$"-c:a aac -b:a 128k -ar 44100 -movflags +faststart \"{outputPath}\"";

				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = arguments,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				using var process = new Process { StartInfo = startInfo };
				process.Start();
				await process.WaitForExitAsync();

				if (process.ExitCode == 0 && File.Exists(outputPath))
				{
					long newSize = new FileInfo(outputPath).Length;
					logger?.LogInformation("✅ [FFmpeg] Видео успешно сжато: {Old:F1} МБ -> {New:F1} МБ",
						fileInfo.Length / (1024.0 * 1024.0), newSize / (1024.0 * 1024.0));

					File.Delete(inputPath);
					File.Move(outputPath, inputPath);
					return inputPath;
				}
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Ошибка сжатия видео для Direct");
			}

			return inputPath;
		}

		/// <summary>
		/// Быстрая конвертация голосового сообщения из браузера (WebM, Ogg, Wav) в эталонный формат Instagram Audio (AAC m4a)
		/// </summary>
		public static async Task<string> ConvertToInstagramAudioAsync(string inputPath, ILogger? logger = null)
		{
			if (!File.Exists(inputPath)) return inputPath;

			string directory = Path.GetDirectoryName(inputPath)!;
			string outputPath = Path.Combine(directory, $"{Guid.NewGuid()}.m4a");

			try
			{
				// Переводим аудиодорожку в стандартный AAC кодек со стерео/моно 44.1kHz
				var arguments = $"-y -i \"{inputPath}\" -vn -c:a aac -b:a 128k -ar 44100 \"{outputPath}\"";

				var startInfo = new ProcessStartInfo
				{
					FileName = "ffmpeg",
					Arguments = arguments,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				using var process = new Process { StartInfo = startInfo };
				process.Start();
				await process.WaitForExitAsync();

				if (process.ExitCode == 0 && File.Exists(outputPath))
				{
					try { File.Delete(inputPath); } catch { }
					logger?.LogInformation("✅ [FFmpeg] Голосовое успешно конвертировано в AAC M4A");
					return outputPath;
				}
			}
			catch (Exception ex)
			{
				logger?.LogError(ex, "Ошибка конвертации аудио для Instagram");
			}

			return inputPath;
		}
	}
}
