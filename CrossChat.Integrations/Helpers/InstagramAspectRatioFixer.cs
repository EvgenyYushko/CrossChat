using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

public static class InstagramAspectRatioFixer
{
	private const double MIN_ASPECT_RATIO = 0.80;  // 4:5
	private const double MAX_ASPECT_RATIO = 1.91;  // 1.91:1

	// Оптимальный кроп: 45% сверху, 55% снизу
	private const double TOP_CROP_RATIO = 0.45;

	// Максимальная ширина для Instagram (лимит платформы 1080-1440px)
	private const int MAX_INSTAGRAM_WIDTH = 1440;

	public static string FixAspectRatioIfNeeded(string base64Image)
	{
		// Если это видео — пропускаем (видео очищается через FFmpeg в SaveMediaLocallyAsync)
		if (base64Image.Contains("video/mp4") || base64Image.Contains("video/"))
		{
			return base64Image;
		}

		try
		{
			string prefix = "";
			string cleanBase64 = base64Image;

			if (base64Image.Contains(","))
			{
				var parts = base64Image.Split(',');
				prefix = parts[0] + ",";
				cleanBase64 = parts[1];
			}

			byte[] imageBytes = Convert.FromBase64String(cleanBase64);
			using var image = Image.Load(imageBytes);

			// === 1. ГАРАНТИРОВАННОЕ УДАЛЕНИЕ МЕТОК ИИ (C2PA, EXIF, IPTC, XMP) ===
			// Зануляем все скрытые профили метаданных, куда генераторы вшивают инфу об ИИ
			image.Metadata.ExifProfile = null;
			image.Metadata.IptcProfile = null;
			image.Metadata.XmpProfile = null;

			double currentRatio = (double)image.Width / image.Height;

			// === 2. КРОП (только если пропорции выходят за рамки) ===
			if (currentRatio < MIN_ASPECT_RATIO)
			{
				int targetHeight = (int)Math.Floor(image.Width / MIN_ASPECT_RATIO);
				int excessHeight = image.Height - targetHeight;

				int cropTop = (int)Math.Round(excessHeight * TOP_CROP_RATIO);
				cropTop = Math.Clamp(cropTop, 0, excessHeight);

				var cropArea = new Rectangle(0, cropTop, image.Width, targetHeight);
				image.Mutate(ctx => ctx.Crop(cropArea));
			}
			else if (currentRatio > MAX_ASPECT_RATIO)
			{
				int targetWidth = (int)Math.Floor(image.Height * MAX_ASPECT_RATIO);
				int excessWidth = image.Width - targetWidth;
				int cropLeft = excessWidth / 2;

				var cropArea = new Rectangle(cropLeft, 0, targetWidth, image.Height);
				image.Mutate(ctx => ctx.Crop(cropArea));
			}

			// === 3. РЕСАЙЗ (только если фото слишком огромное с камеры > 1440px) ===
			if (image.Width > MAX_INSTAGRAM_WIDTH)
			{
				int newWidth = MAX_INSTAGRAM_WIDTH;
				int newHeight = (int)Math.Round((double)image.Height * MAX_INSTAGRAM_WIDTH / image.Width);

				image.Mutate(ctx => ctx.Resize(newWidth, newHeight));
			}

			// === 4. ПЕРЕСБОРКА ФАЙЛА В ЧИСТЫЙ JPEG ===
			// Пересохраняем файл с высоким качеством 95% (визуально без потерь).
			// При этом формируется абсолютно новый заголовок JPEG без единого цифрового следа ИИ!
			using var ms = new MemoryStream();
			var encoder = new JpegEncoder { Quality = 100 };
			image.SaveAsJpeg(ms, encoder);

			return prefix + Convert.ToBase64String(ms.ToArray());
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Instagram Fixer] Ошибка при обработке фото: {ex.Message}");
			return base64Image;
		}
	}
}