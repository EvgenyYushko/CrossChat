using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services
{
	public class XService : IXService
	{
		private readonly ILogger<XService> _logger;

		public XService(ILogger<XService> logger)
		{
			_logger = logger;
		}

		public async Task<bool> CreateTextPostAsync(string text, string accessToken)
		{
			// ВАЖНО: Для OAuth 2.0 (Bearer) можно использовать простой HttpClient
			// Tweetinvi хорош для OAuth 1.0a, но для 2.0 проще так:
			using var client = new HttpClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

			var payload = new { text = text };
			var response = await client.PostAsJsonAsync("https://api.twitter.com/2/tweets", payload);

			return response.IsSuccessStatusCode;
		}

		public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string xClientId, string xClientSecret)
		{
			var tokenUrl = "https://api.twitter.com/2/oauth2/token";

			// X требует Basic Auth заголовок: Base64(ClientId:ClientSecret)
			var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{xClientId}:{xClientSecret}"));

			var values = new Dictionary<string, string>
			{
				{ "grant_type", "refresh_token" },
				{ "refresh_token", refreshToken },
				{ "client_id", xClientId }
			};

			var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
			{
				Content = new FormUrlEncodedContent(values)
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

			try
			{
				using var client = new HttpClient();
				var response = await client.SendAsync(request);
				var json = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					_logger.LogError($"[X Refresh] Ошибка обновления: {json}");
					return null;
				}

				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				return (
					root.GetProperty("access_token").GetString()!,
					root.GetProperty("refresh_token").GetString()!,
					root.GetProperty("expires_in").GetInt32()
				);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[X Refresh] Критическая ошибка");
				return null;
			}
		}
	}
}
