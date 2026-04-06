using System.Net.Http.Json;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
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
