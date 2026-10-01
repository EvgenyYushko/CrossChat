using System.Net.Http.Json;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Site;
using CrossChat.Integrations.Models.Threads;
using Microsoft.Extensions.Logging;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Services;

public partial class ThreadsService : IThreadsService
{
	private readonly HttpClient _httpClient;
	private readonly SiteSettings _siteSettings;
	private readonly ILogger<ThreadsService> _logger;

	public ThreadsService(ILogger<ThreadsService> logger, HttpClient httpClient, SiteSettings siteSettings)
	{
		_logger = logger;
		_httpClient = httpClient;
		_siteSettings = siteSettings;
	}

	public async Task<ThreadsUserProfile?> GetThreadsUserProfileAsync(string accessToken)
	{
		var url = $"https://graph.threads.net/me?fields=id,username,threads_profile_picture_url&access_token={accessToken}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				var errorContent = await response.Content.ReadAsStringAsync();
				_logger.LogError($"[Threads API Error] Не удалось получить профиль: {errorContent}");
				return null;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			var profile = new ThreadsUserProfile(
				Id: root.GetProperty("id").GetString() ?? "",
				Username: root.GetProperty("username").GetString() ?? "unknown",
				ProfilePictureUrl: root.TryGetProperty("threads_profile_picture_url", out var p) ? p.GetString() : null
			);

			_logger.LogInformation($"[Threads] Получен профиль: {profile.Username} (ID: {profile.Id})");
			return profile;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[Threads] Ошибка при запросе профиля пользователя");
			return null;
		}
	}

	public async Task<List<ThreadsItem>> GetUserThreadsAsync(string accessToken)
	{
		var url = $"me/threads?fields=id,has_replies&access_token={accessToken}";
		var resp = await _httpClient.GetFromJsonAsync<ThreadsMediaResponse>(url);
		return resp?.Data ?? new List<ThreadsItem>();
	}

	public async Task<List<ThreadsItem>> GetConversationAsync(string mediaId, string accessToken)
	{
		var url = $"{mediaId}/conversation?fields=id,text,username,replied_to,is_reply_owned_by_me&access_token={accessToken}";
		var resp = await _httpClient.GetFromJsonAsync<ThreadsMediaResponse>(url);
		return resp?.Data ?? new List<ThreadsItem>();
	}

	public async Task<string> CreateReplyContainerAsync(string targetMediaId, string text, string accessToken)
	{
		var url = $"/me/threads?access_token={accessToken}";
		var payload = new { media_type = "TEXT", text = text, reply_to_id = targetMediaId };
		var resp = await _httpClient.PostAsJsonAsync(url, payload);
		var content = await resp.Content.ReadFromJsonAsync<JsonElement>();
		return content.GetProperty("id").GetString();
	}

	public async Task PublishReplyAsync(string creationId, string accessToken)
	{
		var url = $"/me/threads_publish?creation_id={creationId}&access_token={accessToken}";
		await _httpClient.PostAsync(url, null);
	}

	public async Task<string> GetContainerStatusAsync(string containerId, string accessToken)
	{
		var statusUrl = $"{containerId}?fields=id,status&access_token={accessToken}";
		var response = await _httpClient.GetAsync(statusUrl);
		var json = await response.Content.ReadAsStringAsync();

		if (response.IsSuccessStatusCode)
		{
			using var doc = JsonDocument.Parse(json);

			var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

			Console.WriteLine($"Статус: {status}, Status Code: {status}");

			return status;
		}

		return "";
	}

	public async Task<bool> WaitForMediaReadyAsync(string containerId, string accessToken, int maxWaitSeconds = 120)
	{
		Console.WriteLine($"Ожидаем готовности медиа {containerId}...");

		var startTime = DateTimeNow;

		while (DateTimeNow - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
		{
			try
			{
				var statusUrl = $"{containerId}?fields=id,status&access_token={accessToken}";
				var response = await _httpClient.GetAsync(statusUrl);
				var json = await response.Content.ReadAsStringAsync();

				Console.WriteLine($"Статус ответ: {json}");

				if (response.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(json);

					var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;

					Console.WriteLine($"Статус: {status}, Status Code: {status}");

					if (status == "FINISHED")
					{
						// ДОПОЛНИТЕЛЬНАЯ ЗАДЕРЖКА после FINISHED
						Console.WriteLine($"✅ Получен статус FINISHED, ждем 15 секунд перед публикацией...");
						await Task.Delay(5000);
						Console.WriteLine($"✅ Медиа {containerId} готово к публикации!");
						return true;
					}
					else if (status == "ERROR")
					{
						Console.WriteLine($"❌ Медиа {containerId} завершилось с ошибкой");
						return false;
					}

					Console.WriteLine($"⏳ Медиа {containerId} еще обрабатывается...");
				}
				else
				{
					Console.WriteLine($"Ошибка запроса статуса: {json}");
				}

				await Task.Delay(3000);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка при проверке статуса: {ex.Message}");
				await Task.Delay(3000);
			}
		}

		Console.WriteLine($"⏰ Таймаут ожидания медиа {containerId}");
		return false;
	}

	public async Task ReplyToThreadAsync(string targetMediaId, string text, string accessToken)
	{
		// Шаг А: Создаем контейнер текста с привязкой к сообщению пользователя
		var createUrl = $"https://graph.threads.net/v1.0/me/threads";
		var payload = new
		{
			media_type = "TEXT",
			text = text,
			reply_to_id = targetMediaId // Ссылка на то, на что отвечаем
		};

		var response = await _httpClient.PostAsJsonAsync($"{createUrl}?access_token={accessToken}", payload);
		var content = await response.Content.ReadAsStringAsync();

		using var doc = JsonDocument.Parse(content);
		var creationId = doc.RootElement.GetProperty("id").GetString();

		// Шаг Б: Публикуем этот контейнер
		var publishUrl = $"https://graph.threads.net/v1.0/me/threads_publish?creation_id={creationId}&access_token={accessToken}";
		await _httpClient.PostAsync(publishUrl, null);
	}

	public async Task<(string NewToken, int ExpiresIn)?> RefreshTokenAsync(string currentToken)
	{
		// Важно: домен threads.net и grant_type=th_refresh_token
		var url = $"refresh_access_token?grant_type=th_refresh_token&access_token={currentToken}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			var content = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError($"[TokenRefresh] Ошибка обновления токена: {content}");
				return null;
			}

			using var doc = JsonDocument.Parse(content);
			var root = doc.RootElement;

			var newToken = root.GetProperty("access_token").GetString();
			var expiresIn = root.GetProperty("expires_in").GetInt32();

			return (newToken, expiresIn);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[TokenRefresh] Критическая ошибка запроса.");
			return null;
		}
	}

	/// <summary>
	/// Получает ленту постов Threads с курсорной пагинацией
	/// </summary>
	public async Task<ThreadsFeedPageDto> GetAccountFeedAsync(string accessToken, int limit = 12, string? after = null, string? before = null)
	{
		var result = new ThreadsFeedPageDto();
		var fields = "id,text,media_type,media_url,thumbnail_url,permalink,timestamp";
		var url = $"https://graph.threads.net/v1.0/me/threads?fields={fields}&limit={limit}&access_token={accessToken}";

		if (!string.IsNullOrEmpty(after)) url += $"&after={after}";
		else if (!string.IsNullOrEmpty(before)) url += $"&before={before}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				var err = await response.Content.ReadAsStringAsync();
				_logger.LogError("[Threads Analytics] Ошибка загрузки ленты: {Err}", err);
				return result;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;

			if (root.TryGetProperty("data", out var dataArr))
			{
				foreach (var item in dataArr.EnumerateArray())
				{
					var post = new ThreadsFeedPostDto
					{
						Id = item.GetProperty("id").GetString()!,
						MediaType = item.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "TEXT_POST" : "TEXT_POST",
						MediaUrl = item.TryGetProperty("media_url", out var mu) ? mu.GetString() : null,
						ThumbnailUrl = item.TryGetProperty("thumbnail_url", out var tu) ? tu.GetString() : null,
						Permalink = item.TryGetProperty("permalink", out var pl) ? pl.GetString() : null,
						Text = item.TryGetProperty("text", out var t) ? t.GetString() : null
					};

					if (item.TryGetProperty("timestamp", out var ts) && DateTime.TryParse(ts.GetString(), out var dt))
					{
						post.Timestamp = dt;
					}

					result.Posts.Add(post);
				}
			}

			if (root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("cursors", out var cursors))
			{
				if (cursors.TryGetProperty("after", out var afterProp)) result.AfterCursor = afterProp.GetString();
				if (cursors.TryGetProperty("before", out var beforeProp)) result.BeforeCursor = beforeProp.GetString();
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[Threads Analytics] Исключение при получении ленты");
		}

		return result;
	}

	/// <summary>
	/// Получает детальные инсайты конкретного треда (просмотры, лайки, реплаи, репосты, цитаты) и рассчитывает True ER
	/// </summary>
	public async Task<ThreadsPostInsightsDto> GetThreadInsightsAsync(string threadMediaId, string accessToken)
	{
		var insights = new ThreadsPostInsightsDto();
		string metrics = "views,likes,replies,reposts,quotes";
		string url = $"https://graph.threads.net/v1.0/{threadMediaId}/insights?metric={metrics}&access_token={accessToken}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			if (!response.IsSuccessStatusCode)
			{
				// Фоллбек без quotes, если тред старый
				url = $"https://graph.threads.net/v1.0/{threadMediaId}/insights?metric=views,likes,replies,reposts&access_token={accessToken}";
				response = await _httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode) return insights;
			}

			var json = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("data", out var dataArr))
			{
				foreach (var m in dataArr.EnumerateArray())
				{
					var name = m.GetProperty("name").GetString();
					int val = 0;

					if (m.TryGetProperty("total_value", out var totalVal) && totalVal.TryGetProperty("value", out var v))
					{
						val = v.GetInt32();
					}
					else if (m.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
					{
						val = vals[0].GetProperty("value").GetInt32();
					}

					switch (name)
					{
						case "views": insights.Views = val; break;
						case "likes": insights.Likes = val; break;
						case "replies": insights.Replies = val; break;
						case "reposts": insights.Reposts = val; break;
						case "quotes": insights.Quotes = val; break;
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "[Threads Insights] Не удалось получить статистику треда {MediaId}", threadMediaId);
		}

		// Расчет True ER по просмотрам (Views)
		if (insights.Views > 0 && insights.TotalInteractions > 0)
		{
			insights.EngagementRate = Math.Round(((double)insights.TotalInteractions / insights.Views) * 100.0, 2);

			if (insights.EngagementRate >= 15.0)
			{
				insights.EngagementBadge = "🚀 Вирусный тред";
				insights.BadgeColor = "#f43f5e"; // Неоновый рубин
			}
			else if (insights.EngagementRate >= 7.0)
			{
				insights.EngagementBadge = "🔥 Высокий отклик";
				insights.BadgeColor = "#ec4899"; // Фуксия
			}
			else if (insights.EngagementRate >= 3.0)
			{
				insights.EngagementBadge = "⚡ Активная дискуссия";
				insights.BadgeColor = "#10b981"; // Изумрудный зеленый
			}
			else if (insights.EngagementRate >= 1.0)
			{
				insights.EngagementBadge = "👍 Хороший результат";
				insights.BadgeColor = "#38bdf8"; // Небесно-голубой
			}
			else
			{
				insights.EngagementBadge = "💤 Обычный тред";
				insights.BadgeColor = "#94a3b8"; // Нейтральный
			}
		}

		return insights;
	}

	/// <summary>
	/// Получает сводную аналитику аккаунта Threads (суммарные просмотры, лайки, репосты и количество подписчиков)
	/// </summary>
	public async Task<ThreadsAccountInsightsDto> GetAccountInsightsAsync(string accessToken)
	{
		var insights = new ThreadsAccountInsightsDto();
		string metrics = "views,likes,replies,reposts,quotes,followers_count";
		string url = $"https://graph.threads.net/v1.0/me/threads_insights?metric={metrics}&access_token={accessToken}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);

				if (doc.RootElement.TryGetProperty("data", out var dataArr))
				{
					foreach (var m in dataArr.EnumerateArray())
					{
						var name = m.GetProperty("name").GetString();
						int val = 0;

						if (m.TryGetProperty("total_value", out var totalVal) && totalVal.TryGetProperty("value", out var v))
						{
							val = v.GetInt32();
						}
						else if (m.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0)
						{
							val = vals[0].GetProperty("value").GetInt32();
						}

						switch (name)
						{
							case "views": insights.Views = val; break;
							case "likes": insights.Likes = val; break;
							case "replies": insights.Replies = val; break;
							case "reposts": insights.Reposts = val; break;
							case "quotes": insights.Quotes = val; break;
							case "followers_count": insights.FollowersCount = val; break;
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "[Threads Account Insights] Ошибка получения сводной аналитики аккаунта");
		}

		return insights;
	}
}
