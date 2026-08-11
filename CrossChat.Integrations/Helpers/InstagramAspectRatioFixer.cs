using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

public static class InstagramAspectRatioFixer
{
	private const double MIN_ASPECT_RATIO = 0.80;  // 4:5 (минимальное соотношение)
	private const double MAX_ASPECT_RATIO = 1.91;  // 1.91:1 (максимальное соотношение)

	/// <summary>
	/// Проверяет пропорции Base64-изображения. Если они выходят за рамки правил Instagram (0.80 - 1.91),
	/// центрирует фото на белом холсте нужного размера, предотвращая ошибку 2207009.
	/// </summary>
	public static string FixAspectRatioIfNeeded(string base64Image)
	{
		try
		{
			string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;
			byte[] imageBytes = Convert.FromBase64String(cleanBase64);

			using var image = Image.Load(imageBytes);

			double currentRatio = (double)image.Width / image.Height;

			// 1. Если пропорции УЖЕ входят в интервал [0.80 ... 1.91] — возвращаем исходную картинку без изменений!
			if (currentRatio >= MIN_ASPECT_RATIO && currentRatio <= MAX_ASPECT_RATIO)
			{
				return base64Image;
			}

			int newWidth = image.Width;
			int newHeight = image.Height;

			// 2. Слишком узкое/высокое фото (ratio < 0.80, например 694x1260 = 0.55)
			if (currentRatio < MIN_ASPECT_RATIO)
			{
				// Расширяем ширину до соотношения 4:5
				newWidth = (int)Math.Ceiling(image.Height * MIN_ASPECT_RATIO);
			}
			// 3. Слишком широкое панорамное фото (ratio > 1.91)
			else if (currentRatio > MAX_ASPECT_RATIO)
			{
				// Увеличиваем высоту до соотношения 1.91:1
				newHeight = (int)Math.Ceiling(image.Width / MAX_ASPECT_RATIO);
			}

			// 1. Создаем пустой холст нужного размера
			using var canvas = new Image<Rgba32>(newWidth, newHeight);

			int offsetX = (newWidth - image.Width) / 2;
			int offsetY = (newHeight - image.Height) / 2;

			// 2. Заливаем белым цветом и рисуем поверх исходное фото по центру
			canvas.Mutate(ctx => ctx
				.BackgroundColor(Color.White)
				.DrawImage(image, new Point(offsetX, offsetY), 1f));

			// Сохраняем в JPEG
			using var ms = new MemoryStream();
			canvas.SaveAsJpeg(ms);

			return Convert.ToBase64String(ms.ToArray());
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Instagram Fixer] Ошибка при проверке пропорций: {ex.Message}");
			return base64Image; // В случае сбоя возвращаем картинку как есть
		}
	}
}