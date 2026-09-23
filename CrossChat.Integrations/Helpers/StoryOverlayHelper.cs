using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CrossChat.Infrastructure.Helpers
{
	public static class StoryOverlayHelper
	{
		private class StoryBadgeStyle
		{
			public Color BgColor { get; set; }
			public Color BorderColor { get; set; }
			public Color TextColor { get; set; }
		}

		private static readonly StoryBadgeStyle[] BadgeThemes = new[]
		{
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(15, 23, 42, 225),
				BorderColor = Color.FromRgba(99, 102, 241, 200),
				TextColor = Color.White
			},
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(225, 48, 108, 230),
				BorderColor = Color.FromRgba(240, 148, 51, 220),
				TextColor = Color.White
			},
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(255, 255, 255, 235),
				BorderColor = Color.FromRgba(255, 255, 255, 160),
				TextColor = Color.FromRgb(15, 23, 42)
			},
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(88, 28, 135, 230),
				BorderColor = Color.FromRgba(192, 132, 252, 210),
				TextColor = Color.White
			},
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(6, 78, 59, 230),
				BorderColor = Color.FromRgba(52, 211, 153, 210),
				TextColor = Color.White
			}
		};

		public static byte[] OverlayTextOnImage(byte[] imageBytes, string rawTextConfig, ILogger? logger = null)
		{
			string text = PickAndSanitizeRandomText(rawTextConfig);
			if (string.IsNullOrWhiteSpace(text)) return imageBytes;

			using var image = Image.Load(imageBytes);

			FontFamily family;
			if (!SystemFonts.TryGet("Arial", out family) &&
				!SystemFonts.TryGet("DejaVu Sans", out family) &&
				!SystemFonts.TryGet("Segoe UI", out family) &&
				!SystemFonts.TryGet("Liberation Sans", out family))
			{
				family = SystemFonts.Collection.Families.FirstOrDefault();
			}

			if (family == default)
			{
				logger?.LogWarning("[StoryOverlay] Системные шрифты не найдены.");
				return imageBytes;
			}

			float fontSize = Math.Clamp(image.Width * 0.042f, 32f, 76f);
			var font = family.CreateFont(fontSize, FontStyle.Bold);

			var textOptions = new TextOptions(font);
			var textSize = TextMeasurer.MeasureSize(text, textOptions);

			float paddingX = fontSize * 1.2f;
			float paddingY = fontSize * 0.65f;
			float badgeWidth = textSize.Width + (paddingX * 2);
			float badgeHeight = textSize.Height + (paddingY * 2);

			float[] yRatios = new[] { 0.22f, 0.52f, 0.75f };
			float chosenYRatio = yRatios[Random.Shared.Next(yRatios.Length)];

			float badgeX = (image.Width - badgeWidth) / 2f;
			float badgeY = (image.Height - badgeHeight) * chosenYRatio;

			float cornerRadius = badgeHeight / 2f;
			var rect = new RectangleF(badgeX, badgeY, badgeWidth, badgeHeight);

			float textX = badgeX + paddingX;
			float textY = badgeY + paddingY;

			var theme = BadgeThemes[Random.Shared.Next(BadgeThemes.Length)];

			image.Mutate(ctx =>
			{
				var capsuleShape = CreateRoundedRectPath(rect, cornerRadius);
				ctx.Fill(theme.BgColor, capsuleShape);
				ctx.Draw(theme.BorderColor, 2.5f, capsuleShape);

				ctx.DrawText(text, font, theme.TextColor, new PointF(textX, textY));
			});

			using var ms = new MemoryStream();
			image.SaveAsJpeg(ms);
			return ms.ToArray();
		}

		/// <summary>
		/// Создает фигуру скругленного прямоугольника / капсулы через нативные дуги PathBuilder
		/// </summary>
		private static IPath CreateRoundedRectPath(RectangleF rect, float cornerRadius)
		{
			float x = rect.X;
			float y = rect.Y;
			float w = rect.Width;
			float h = rect.Height;
			float r = Math.Min(cornerRadius, Math.Min(w / 2f, h / 2f));

			var builder = new PathBuilder();
			builder.StartFigure();

			builder.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
			builder.AddArc(x + w - r, y + r, r, r, 0, 270, 90);

			builder.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
			builder.AddArc(x + w - r, y + h - r, r, r, 0, 0, 90);

			builder.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
			builder.AddArc(x + r, y + h - r, r, r, 0, 90, 90);

			builder.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
			builder.AddArc(x + r, y + r, r, r, 0, 180, 90);

			builder.CloseFigure();
			return builder.Build();
		}

		private static string PickAndSanitizeRandomText(string rawInput)
		{
			if (string.IsNullOrWhiteSpace(rawInput)) return "";

			string selected = rawInput;

			if (rawInput.TrimStart().StartsWith("["))
			{
				try
				{
					var list = JsonSerializer.Deserialize<List<string>>(rawInput);
					if (list != null && list.Any())
					{
						selected = list[Random.Shared.Next(list.Count)];
					}
				}
				catch { }
			}
			else
			{
				var lines = rawInput
					.Split(new[] { "\r\n", "\r", "\n", "|" }, StringSplitOptions.RemoveEmptyEntries)
					.Select(l => l.Trim())
					.Where(l => !string.IsNullOrEmpty(l))
					.ToList();

				if (lines.Any())
				{
					selected = lines[Random.Shared.Next(lines.Count)];
				}
			}

			// Раскрываем Spintax, если он есть
			selected = ResolveSpintax(selected);

			// Очистка от суррогатных символов
			string cleanText = Regex.Replace(selected, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", "").Trim();
			return string.IsNullOrWhiteSpace(cleanText) ? selected.Trim() : cleanText;
		}

		private static string ResolveSpintax(string text)
		{
			var regex = new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);
			while (regex.IsMatch(text))
			{
				text = regex.Replace(text, match =>
				{
					var options = match.Groups[1].Value.Split('|');
					return options[Random.Shared.Next(options.Length)];
				});
			}
			return text.Replace("{", "").Replace("}", "").Trim();
		}

		/// <summary>
		/// Генерирует прозрачную PNG-картинку стикера с текстом для последующего наложения на видео
		/// </summary>
		public static (byte[] PngBytes, float YRatio) GenerateBadgePng(string rawTextConfig, float baseWidth = 1080f)
		{
			string text = PickAndSanitizeRandomText(rawTextConfig);
			if (string.IsNullOrWhiteSpace(text)) return (Array.Empty<byte>(), 0.52f);

			FontFamily family;
			if (!SystemFonts.TryGet("Arial", out family) &&
				!SystemFonts.TryGet("DejaVu Sans", out family) &&
				!SystemFonts.TryGet("Segoe UI", out family) &&
				!SystemFonts.TryGet("Liberation Sans", out family))
			{
				family = SystemFonts.Collection.Families.FirstOrDefault();
			}

			if (family == default) return (Array.Empty<byte>(), 0.52f);

			float fontSize = Math.Clamp(baseWidth * 0.042f, 32f, 76f);
			var font = family.CreateFont(fontSize, FontStyle.Bold);

			var textOptions = new TextOptions(font);
			var textSize = TextMeasurer.MeasureSize(text, textOptions);

			float paddingX = fontSize * 1.2f;
			float paddingY = fontSize * 0.65f;
			float badgeWidth = textSize.Width + (paddingX * 2);
			float badgeHeight = textSize.Height + (paddingY * 2);

			float cornerRadius = badgeHeight / 2f;
			var rect = new RectangleF(0, 0, badgeWidth, badgeHeight);

			// Создаем абсолютно прозрачный холст точно под размер капсулы
			using var badgeImage = new Image<Rgba32>((int)Math.Ceiling(badgeWidth), (int)Math.Ceiling(badgeHeight));

			var theme = BadgeThemes[Random.Shared.Next(BadgeThemes.Length)];

			badgeImage.Mutate(ctx =>
			{
				var capsuleShape = CreateRoundedRectPath(rect, cornerRadius);
				ctx.Fill(theme.BgColor, capsuleShape);
				ctx.Draw(theme.BorderColor, 2.5f, capsuleShape);

				ctx.DrawText(text, font, theme.TextColor, new PointF(paddingX, paddingY));
			});

			using var ms = new MemoryStream();
			badgeImage.SaveAsPng(ms);

			// Случайная высота (верх, центр или низ)
			float[] yRatios = new[] { 0.22f, 0.52f, 0.75f };
			float chosenYRatio = yRatios[Random.Shared.Next(yRatios.Length)];

			return (ms.ToArray(), chosenYRatio);
		}
	}
}