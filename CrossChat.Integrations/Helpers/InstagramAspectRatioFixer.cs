using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

public static class InstagramAspectRatioFixer
{
	private const double MIN_ASPECT_RATIO = 0.80;  // 4:5 (портрет)
	private const double MAX_ASPECT_RATIO = 1.91;  // 1.91:1 (пейзаж)

	// Доля обрезки сверху для вертикальных фото .
	// Это оставляет воздух над головой (Headroom) и сохраняет прическу/макушку целыми.
	// 45% срезаем сверху, 55% снизу (максимально сбалансированный кроп)
	private const double TOP_CROP_RATIO = 0.45;

	/// <summary>
	/// Проверяет пропорции изображения для ленты Instagram.
	/// Если фото выходит за рамки [0.80 ... 1.91], аккуратно кадрирует (обрезает) его без добавления белых полей:
	/// - Вертикальные фото (3:4, 9:16) обрезаются с акцентом на сохранение верхней части (головы).
	/// - Панорамные фото обрезаются симметрично по бокам.
	/// </summary>
	public static string FixAspectRatioIfNeeded(string base64Image)
	{
		// Если это видео — пропускаем без обработки, видео нельзя кадрировать через ImageSharp!
		if (base64Image.Contains("video/mp4") || base64Image.Contains("video/"))
		{
			return base64Image;
		}

		try
		{
			string prefix = "";
			string cleanBase64 = base64Image;

			// Сохраняем data-uri префикс, если он был передан (например "data:image/jpeg;base64,")
			if (base64Image.Contains(","))
			{
				var parts = base64Image.Split(',');
				prefix = parts[0] + ",";
				cleanBase64 = parts[1];
			}

			byte[] imageBytes = Convert.FromBase64String(cleanBase64);
			using var image = Image.Load(imageBytes);

			double currentRatio = (double)image.Width / image.Height;

			// 1. Если пропорции УЖЕ допустимы в Instagram [0.80 ... 1.91] — не трогаем фото!
			if (currentRatio >= MIN_ASPECT_RATIO && currentRatio <= MAX_ASPECT_RATIO)
			{
				return base64Image;
			}

			Rectangle cropArea;

			// 2. Слишком высокое фото (ratio < 0.80, например 3:4 = 0.75 или 9:16 = 0.56)
			if (currentRatio < MIN_ASPECT_RATIO)
			{
				// Вычисляем целевую высоту для соотношения ровно 4:5 (0.80)
				// Math.Floor гарантирует, что итоговое соотношение не станет 0.7999
				int targetHeight = (int)Math.Floor(image.Width / MIN_ASPECT_RATIO);
				int excessHeight = image.Height - targetHeight;

				// Срезаем 30% лишнего сверху, 70% снизу
				int cropTop = (int)Math.Round(excessHeight * TOP_CROP_RATIO);

				// Страховка от выхода за границы изображения
				cropTop = Math.Clamp(cropTop, 0, excessHeight);

				cropArea = new Rectangle(0, cropTop, image.Width, targetHeight);
			}
			// 3. Слишком широкое панорамное фото (ratio > 1.91)
			else
			{
				// Вычисляем целевую ширину для соотношения 1.91:1
				int targetWidth = (int)Math.Floor(image.Height * MAX_ASPECT_RATIO);
				int excessWidth = image.Width - targetWidth;

				// Для пейзажей центрируем обрезку по бокам (50% слева, 50% справа)
				int cropLeft = excessWidth / 2;

				cropArea = new Rectangle(cropLeft, 0, targetWidth, image.Height);
			}

			// Выполняем обрезку прямо на исходном изображении (без создания белых полос)
			image.Mutate(ctx => ctx.Crop(cropArea));

			using var ms = new MemoryStream();
			image.SaveAsJpeg(ms);

			return prefix + Convert.ToBase64String(ms.ToArray());
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Instagram Fixer] Ошибка при кадрировании: {ex.Message}");
			return base64Image; // В случае непредвиденного сбоя отдаем исходник
		}
	}
}