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
