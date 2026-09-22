using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class InstagramCommentEngine
{
    private static readonly Regex SpintaxRegex = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Главный метод: достает фразу из переменной БД, раскрывает {а|б} и оживляет текст
    /// </summary>
    public static string? GetRandomTemplate(string? rawTemplates)
    {
        if (string.IsNullOrWhiteSpace(rawTemplates)) return null;

        // 1. Достаем случайную строку (поддерживает и обычный список, и JSON, и разделитель "|")
        string? selected = PickRandomLine(rawTemplates);
        if (string.IsNullOrWhiteSpace(selected)) return null;

        // 2. Раскрываем Spintax: {пасиб|спасибо}
        selected = ResolveSpintax(selected);

        // 3. Добавляем щепотку "человечности" (скобочки, строчная буква)
        selected = AddHumanSalt(selected);

        return selected;
    }

    private static string? PickRandomLine(string raw)
    {
        var trimmed = raw.Trim();

        // Если в БД случайно лежит JSON-массив: ["фраза1", "фраза2"]
        if (trimmed.StartsWith("["))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (list != null && list.Count > 0)
                    return list[Random.Shared.Next(list.Count)].Trim();
            }
            catch { }
        }

        // Если строки через перенос или "|"
        var lines = trimmed
            .Split(new[] { "\r\n", "\r", "\n", "|" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        return lines.Count > 0 ? lines[Random.Shared.Next(lines.Count)] : trimmed;
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

        // 1. Делаем первую букву маленькой, ТОЛЬКО если это буква (не ломает эмодзи)
        if (Random.Shared.Next(100) < 85 && text.Length > 0 && char.IsLetter(text[0]))
        {
            text = char.ToLower(text[0]) + text.Substring(1);
        }

        // 2. Живые скобочки в конце: рандомим от 1 до 3 скобок
        if (text.EndsWith(")"))
        {
            text = text.TrimEnd(')');
            text += new string(')', Random.Shared.Next(1, 4));
        }
        // 3. Многоточия: случайно варьируем ".." или "..."
        else if (text.EndsWith(".."))
        {
            text = text.TrimEnd('.') + (Random.Shared.Next(2) == 0 ? ".." : "...");
        }

        return text.Trim();
    }
}