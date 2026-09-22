using System.Text.Json;
using System.Text.RegularExpressions;

public static class InstagramCommentEngine
{
	private static readonly Regex SpintaxRegex = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

	// Память на последние использованные фразы (чтобы не повторяться)
	private static readonly Queue<string> RecentTemplates = new();
	private const int MaxRecentMemory = 5;

	public static string? GetRandomTemplate(string? rawTemplates)
	{
		if (string.IsNullOrWhiteSpace(rawTemplates)) return null;

		// 1. Достаем строку
		string? selected = PickRandomLineWithAntiRepeat(rawTemplates);
		if (string.IsNullOrWhiteSpace(selected)) return null;

		// 2. Раскрываем Spintax: {пасиб|спасибо}
		selected = ResolveSpintax(selected);

		// СТРАХОВКА: если где-то осталась фигурная скобка, стираем её намертво
		selected = selected.Replace("{", "").Replace("}", "").Trim();

		// 3. Солим (скобочки, точки)
		selected = AddHumanSalt(selected);

		return selected;
	}

	private static string? PickRandomLineWithAntiRepeat(string raw)
	{
		var trimmed = raw.Trim();
		List<string>? lines = null;

		// Читаем JSON массив [...]
		if (trimmed.StartsWith("["))
		{
			try
			{
				lines = JsonSerializer.Deserialize<List<string>>(trimmed);
			}
			catch { }
		}

		// Если обычный текст — делим ТОЛЬКО по переносам строк (БЕЗ "|")
		if (lines == null || lines.Count == 0)
		{
			lines = trimmed
				.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => !string.IsNullOrEmpty(l))
				.ToList();
		}

		if (lines == null || lines.Count == 0) return null;

		// Анти-повтор
		string candidate = lines[Random.Shared.Next(lines.Count)];
		int attempts = 0;

		lock (RecentTemplates)
		{
			while (RecentTemplates.Contains(candidate) && attempts < 10 && lines.Count > 1)
			{
				candidate = lines[Random.Shared.Next(lines.Count)];
				attempts++;
			}

			RecentTemplates.Enqueue(candidate);
			if (RecentTemplates.Count > MaxRecentMemory)
			{
				RecentTemplates.Dequeue();
			}
		}

		return candidate;
	}

	public static string ResolveSpintax(string text)
	{
		while (SpintaxRegex.IsMatch(text))
		{
			text = SpintaxRegex.Replace(text, match =>
			{
				var options = match.Groups[1].Value.Split('|');
				return options[Random.Shared.Next(options.Length)];
			});
		}
		return text;
	}

	public static string AddHumanSalt(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return text;

		text = text.Trim();

		// 1. Делаем первую букву маленькой (85% шанс), если это буква
		if (Random.Shared.Next(100) < 85 && text.Length > 0 && char.IsLetter(text[0]))
		{
			text = char.ToLower(text[0]) + text.Substring(1);
		}

		// 2. ЕСЛИ В КОНЦЕ ИЗНАЧАЛЬНО СКОБКА ")"
		if (text.EndsWith(")"))
		{
			text = text.TrimEnd(')');

			// 20% шанс ВООБЩЕ убрать скобку (оставить голое слово!)
			if (Random.Shared.Next(100) < 20)
			{
				return text.Trim();
			}

			// Иначе ставим от 1 до 3 скобочек
			text += new string(')', Random.Shared.Next(1, 4));
			return text;
		}

		// 3. ЕСЛИ В КОНЦЕ МНОГОТОЧИЕ ".." или "..."
		if (text.EndsWith("..") || text.EndsWith("..."))
		{
			text = text.TrimEnd('.');
			int dotRoll = Random.Shared.Next(100);

			if (dotRoll < 20) return text.Trim(); // 20% шанс убрать точки вовсе
			text += (dotRoll < 60) ? ".." : "...";
			return text;
		}

		// 4. ЕСЛИ В КОНЦЕ СМАЙЛИК (🙈, 🥰 и т.д.)
		if (!char.IsLetterOrDigit(text[^1]) && text[^1] != '!' && text[^1] != '?')
		{
			// Только в 30% случаев прилепляем скобку после смайла (🙈)), в 70% оставляем как есть
			if (Random.Shared.Next(100) < 30)
			{
				text += new string(')', Random.Shared.Next(1, 3));
			}
			return text;
		}

		// 5. ЕСЛИ В КОНЦЕ ОБЫЧНОЕ СЛОВО БЕЗ ЗНАКОВ:
		if (char.IsLetterOrDigit(text[^1]))
		{
			int roll = Random.Shared.Next(100);

			if (roll < 45) // 45% — скобочки ) или ))
			{
				text += new string(')', Random.Shared.Next(1, 3));
			}
			else if (roll < 70) // 25% — ОСТАВЛЯЕМ ГОЛЫМ СЛОВОМ (вообще без знаков!)
			{
				// ничего не добавляем, выходит чистый текст
			}
			else if (roll < 88) // 18% — многоточие
			{
				text += Random.Shared.Next(2) == 0 ? ".." : "...";
			}
			else // 12% — восклицательный знак
			{
				text += "!";
			}
		}

		return text.Trim();
	}
}