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
			// 1. Neon Graphite (Темный с неоном)
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(15, 23, 42, 225),
				BorderColor = Color.FromRgba(99, 102, 241, 200),
				TextColor = Color.White
			},
			// 2. Instagram Sunset (Розово-пурпурный)
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(225, 48, 108, 230),
				BorderColor = Color.FromRgba(240, 148, 51, 220),
				TextColor = Color.White
			},
			// 3. Frosted Glass (Белое матовое стекло с темным текстом)
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(255, 255, 255, 235),
				BorderColor = Color.FromRgba(255, 255, 255, 160),
				TextColor = Color.FromRgb(15, 23, 42)
			},
			// 4. Cyber Violet (Фиолетовый неон)
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(88, 28, 135, 230),
				BorderColor = Color.FromRgba(192, 132, 252, 210),
				TextColor = Color.White
			},
			// 5. Emerald Luxury (Изумруд)
			new StoryBadgeStyle {
				BgColor = Color.FromRgba(6, 78, 59, 230),
				BorderColor = Color.FromRgba(52, 211, 153, 210),
				TextColor = Color.White
			}
		};

		/// <summary>
		/// Наложение стилизованного стикера с автопереносом и наклоном на ФОТО
		/// </summary>
		public static byte[] OverlayTextOnImage(byte[] imageBytes, string rawTextConfig, ILogger? logger = null)
		{
			using var baseImage = Image.Load(imageBytes);

			// Создаем готовый прозрачный стикер с учетом ширины фото
			using var badgeImage = CreateBadgeImage(rawTextConfig, baseImage.Width, out float yRatio);
			if (badgeImage == null) return imageBytes;

			// Центрируем по горизонтали и позиционируем по случайной высоте
			float posX = (baseImage.Width - badgeImage.Width) / 2f;
			float posY = (baseImage.Height - badgeImage.Height) * yRatio;

			baseImage.Mutate(ctx =>
			{
				ctx.DrawImage(badgeImage, new Point((int)posX, (int)posY), 1f);
			});

			using var ms = new MemoryStream();
			baseImage.SaveAsJpeg(ms);
			return ms.ToArray();
		}

		/// <summary>
		/// Генерация прозрачного PNG-стикера с автопереносом и наклоном для ВИДЕО (FFmpeg)
		/// </summary>
		public static (byte[] PngBytes, float YRatio) GenerateBadgePng(string rawTextConfig, float baseWidth = 1080f)
		{
			using var badgeImage = CreateBadgeImage(rawTextConfig, baseWidth, out float yRatio);
			if (badgeImage == null) return (Array.Empty<byte>(), 0.52f);

			using var ms = new MemoryStream();
			badgeImage.SaveAsPng(ms);
			return (ms.ToArray(), yRatio);
		}

		/// <summary>
		/// Фабрика создания стикера: перенос слов, строгое центрирование каждой строки и случайный наклон
		/// </summary>
		private static Image<Rgba32>? CreateBadgeImage(string rawTextConfig, float targetCanvasWidth, out float yRatio)
		{
			yRatio = 0.52f;
			string rawText = PickAndSanitizeRandomText(rawTextConfig);
			if (string.IsNullOrWhiteSpace(rawText)) return null;

			// 1. Подбираем системный шрифт
			FontFamily family;
			if (!SystemFonts.TryGet("Arial", out family) &&
				!SystemFonts.TryGet("DejaVu Sans", out family) &&
				!SystemFonts.TryGet("Segoe UI", out family) &&
				!SystemFonts.TryGet("Liberation Sans", out family))
			{
				family = SystemFonts.Collection.Families.FirstOrDefault();
			}

			if (family == default) return null;

			// 2. Размер шрифта (3.8% от ширины)
			float fontSize = Math.Clamp(targetCanvasWidth * 0.038f, 28f, 64f);
			var font = family.CreateFont(fontSize, FontStyle.Bold);

			// 3. Ограничиваем максимальную ширину текста (не более 76% от ширины экрана)
			float maxAllowedTextWidth = targetCanvasWidth * 0.76f;

			// 4. Логический перенос слов по строкам (Word-Wrap)
			var lines = WrapTextByWords(rawText, font, maxAllowedTextWidth);

			// Если получилось слишком много строк — уменьшаем шрифт на 15% и переносим заново
			if (lines.Count > 3)
			{
				fontSize *= 0.85f;
				font = family.CreateFont(fontSize, FontStyle.Bold);
				lines = WrapTextByWords(rawText, font, maxAllowedTextWidth);
			}

			// 5. Замеряем каждую строчку индивидуально для идеального центрирования
			var textOptions = new TextOptions(font);
			var lineSizes = lines.Select(l => TextMeasurer.MeasureSize(l, textOptions)).ToList();

			float maxLineWidth = lineSizes.Max(s => s.Width);
			float lineHeight = lineSizes.Max(s => s.Height);
			float lineSpacing = fontSize * 0.22f;
			float totalTextHeight = (lineHeight * lines.Count) + (lineSpacing * (lines.Count - 1));

			// 6. Размеры и отступы плашки
			float paddingX = fontSize * 1.25f;
			float paddingY = fontSize * 0.75f;
			float badgeWidth = maxLineWidth + (paddingX * 2);
			float badgeHeight = totalTextHeight + (paddingY * 2);

			// Если строка 1 — идеальная капсула (pill). Если несколько — стильный закругленный бейдж (24px)
			float cornerRadius = lines.Count == 1 ? (badgeHeight / 2f) : Math.Min(26f, badgeHeight / 3f);
			var badgeRect = new RectangleF(0, 0, badgeWidth, badgeHeight);

			// 7. Отрисовываем плашку и текст на прозрачном холсте
			var badgeImage = new Image<Rgba32>((int)Math.Ceiling(badgeWidth), (int)Math.Ceiling(badgeHeight));
			var theme = BadgeThemes[Random.Shared.Next(BadgeThemes.Length)];

			badgeImage.Mutate(ctx =>
			{
				var shape = CreateRoundedRectPath(badgeRect, cornerRadius);
				ctx.Fill(theme.BgColor, shape);
				ctx.Draw(theme.BorderColor, 2.5f, shape);

				// Рисуем каждую строку СТРОГО ПО ЦЕНТРУ плашки!
				for (int i = 0; i < lines.Count; i++)
				{
					string lineText = lines[i];
					float currentLineWidth = lineSizes[i].Width;

					// Математический центр для каждой строки:
					float textX = (badgeWidth - currentLineWidth) / 2f;
					float textY = paddingY + (i * (lineHeight + lineSpacing));

					ctx.DrawText(lineText, font, theme.TextColor, new PointF(textX, textY));
				}
			});

			// 8. ЧЕЛОВЕЧНЫЙ НАКЛОН (Случайный поворот от -3.5° до +3.5°)
			float[] tiltAngles = new[] { -3.5f, -2.2f, -1.2f, 0f, 0f, 1.2f, 2.2f, 3.5f };
			float randomAngle = tiltAngles[Random.Shared.Next(tiltAngles.Length)];

			if (Math.Abs(randomAngle) > 0.1f)
			{
				badgeImage.Mutate(ctx => ctx.Rotate(randomAngle));
			}

			// 9. Случайная вертикальная позиция (Верх 22%, Центр 52% или Низ 76%)
			float[] yRatios = new[] { 0.22f, 0.52f, 0.76f };
			yRatio = yRatios[Random.Shared.Next(yRatios.Length)];

			return badgeImage;
		}

		/// <summary>
		/// Алгоритм аккуратного переноса по словам с сохранением смысла
		/// </summary>
		private static List<string> WrapTextByWords(string text, Font font, float maxLineWidth)
		{
			var resultLines = new List<string>();
			var textOptions = new TextOptions(font);

			var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (words.Length == 0) return resultLines;

			string currentLine = words[0];

			for (int i = 1; i < words.Length; i++)
			{
				string testLine = currentLine + " " + words[i];
				var size = TextMeasurer.MeasureSize(testLine, textOptions);

				if (size.Width <= maxLineWidth)
				{
					currentLine = testLine;
				}
				else
				{
					resultLines.Add(currentLine);
					currentLine = words[i];
				}
			}

			if (!string.IsNullOrEmpty(currentLine))
			{
				resultLines.Add(currentLine);
			}

			return resultLines;
		}

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
	}
}