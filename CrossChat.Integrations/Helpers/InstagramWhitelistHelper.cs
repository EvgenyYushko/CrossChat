namespace CrossChat.Integrations.Helpers;

public static class InstagramWhitelistHelper
{
	public static string NormalizeToken(string? raw)
		=> (raw ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

	public static HashSet<string> Parse(string? rawWhitelist)
	{
		if (string.IsNullOrWhiteSpace(rawWhitelist))
			return new HashSet<string>();

		return rawWhitelist
			.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(NormalizeToken)
			.Where(t => !string.IsNullOrEmpty(t))
			.ToHashSet();
	}

	public static bool IsInWhitelist(string? rawWhitelist, string? username, string? senderId = null)
	{
		var set = Parse(rawWhitelist);
		if (set.Count == 0) return false;

		string normUser = NormalizeToken(username);
		if (!string.IsNullOrEmpty(normUser) && set.Contains(normUser))
			return true;

		string normId = NormalizeToken(senderId);
		if (!string.IsNullOrEmpty(normId) && set.Contains(normId))
			return true;

		return false;
	}

	public static (string NewWhitelist, bool IsAdded) Toggle(string? rawWhitelist, string? username, string? senderId = null)
	{
		var list = (rawWhitelist ?? string.Empty)
			.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(NormalizeToken)
			.Where(t => !string.IsNullOrEmpty(t))
			.Distinct()
			.ToList();

		string target = !string.IsNullOrWhiteSpace(username)
			? NormalizeToken(username)
			: NormalizeToken(senderId);

		if (string.IsNullOrEmpty(target))
			return (string.Join(", ", list), false);

		string altId = NormalizeToken(senderId);

		if (list.Contains(target) || (!string.IsNullOrEmpty(altId) && list.Contains(altId)))
		{
			list.RemoveAll(x => x == target || (!string.IsNullOrEmpty(altId) && x == altId));
			return (string.Join(", ", list), false);
		}

		list.Add(target);
		return (string.Join(", ", list), true);
	}
}