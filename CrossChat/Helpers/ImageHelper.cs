using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

public static class ImageHelper
{
	/// <summary>
	/// Пропорционально уменьшает разрешение изображения (если оно превышает лимит),
	/// сжимает его без визуальной потери качества и возвращает Base64-строку.
	/// </summary>
	public static async Task<ImageCompressionResult> CompressAndConvertToBase64Async(IFormFile file, int maxDimension = 1600, int quality = 90)
	{
		var result = new ImageCompressionResult();

		// 1. Записываем исходный вес файла в МБ
		result.OriginalSizeMb = file.Length / (1024.0 * 1024.0);

		using var inputStream = file.OpenReadStream();
		using var image = await Image.LoadAsync(inputStream);

		// 2. Записываем исходное разрешение
		result.OriginalWidth = image.Width;
		result.OriginalHeight = image.Height;

		// 3. Умный ресайз
		if (image.Width > maxDimension || image.Height > maxDimension)
		{
			var resizeOptions = new ResizeOptions
			{
				Mode = ResizeMode.Max,
				Size = new Size(maxDimension, maxDimension),
				Sampler = KnownResamplers.Bicubic
			};

			image.Mutate(x => x.Resize(resizeOptions));
		}

		// Записываем итоговое разрешение после ресайза
		result.CompressedWidth = image.Width;
		result.CompressedHeight = image.Height;

		// 4. Сохраняем как оптимизированный JPEG
		using var outputStream = new MemoryStream();
		var encoder = new JpegEncoder
		{
			Quality = quality
		};

		await image.SaveAsJpegAsync(outputStream, encoder);

		// Записываем итоговый вес сжатого JPEG и конвертируем в Base64
		result.CompressedSizeMb = outputStream.Length / (1024.0 * 1024.0);
		result.Base64 = Convert.ToBase64String(outputStream.ToArray());

		return result;
	}
}

public class ImageCompressionResult
{
	public string Base64 { get; set; } = string.Empty;
	public int OriginalWidth { get; set; }
	public int OriginalHeight { get; set; }
	public double OriginalSizeMb { get; set; }
	public int CompressedWidth { get; set; }
	public int CompressedHeight { get; set; }
	public double CompressedSizeMb { get; set; }

	// Вес строки Base64 в БД (в кодировке UTF-8 один символ ASCII занимает ровно 1 байт)
	public double Base64DbSizeMb => Base64.Length / (1024.0 * 1024.0);
}