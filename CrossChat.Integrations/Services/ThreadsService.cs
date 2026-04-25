using System.Net.Http.Json;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services;

public class ThreadsService : IThreadsService
{
	private readonly HttpClient _httpClient;
	private readonly ILogger<ThreadsService> _logger;

	public ThreadsService(ILogger<ThreadsService> logger, HttpClient httpClient)
	{
		_logger = logger;
		_httpClient = httpClient;
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

	public async Task<bool> WaitForMediaReadyAsync(string containerId, string accessToken, int maxWaitSeconds = 60)
	{
		Console.WriteLine($"Ожидаем готовности медиа {containerId}...");

		var startTime = DateTime.Now;

		while (DateTime.Now - startTime < TimeSpan.FromSeconds(maxWaitSeconds))
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
}
