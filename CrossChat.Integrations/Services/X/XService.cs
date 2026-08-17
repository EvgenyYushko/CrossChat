using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using Microsoft.Extensions.Logging;
using Tweetinvi;

namespace CrossChat.Integrations.Services
{
	public partial class XService : IXService
	{
		private readonly ILogger<XService> _logger;
		private readonly TwitterClient _twitterClient;
		private HttpClient _httpClient;

		public XService(ILogger<XService> logger, TwitterClient twitterClient)
		{
			_logger = logger;
			_twitterClient = twitterClient;
			_httpClient = new HttpClient();

			// ОБЯЗАТЕЛЬНО: Притворяемся браузером, чтобы Cloudflare Bot Management не блокировал запросы
			if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
			{
				_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
			}
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

		public async Task<XUserProfile?> GetXUserProfileAsync(string accessToken)
		{
			try
			{
				using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.twitter.com/2/users/me?user.fields=profile_image_url");
				profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				using var client = new HttpClient();

				var profileResponse = await client.SendAsync(profileRequest);
				if (!profileResponse.IsSuccessStatusCode)
				{
					var error = await profileResponse.Content.ReadAsStringAsync();
					_logger.LogError($"[X API Error] Не удалось получить профиль: {error}");
					return null;
				}

				var userJson = await profileResponse.Content.ReadAsStringAsync();
				var userData = JsonDocument.Parse(userJson).RootElement.GetProperty("data");

				return new XUserProfile(
					Id: userData.GetProperty("id").GetString() ?? "",
					Username: userData.GetProperty("username").GetString() ?? "unknown",
					ProfilePictureUrl: userData.TryGetProperty("profile_image_url", out var p) ? p.GetString() : null
				);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[X] Ошибка при запросе профиля через HttpClient");
				return null;
			}
		}

		public async Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string xClientId, string xClientSecret)
		{
			var tokenUrl = "https://api.x.com/2/oauth2/token";

			// 1. Очищаем от случайных пробелов
			var cleanClientId = xClientId.Trim();
			var cleanClientSecret = xClientSecret.Trim();

			// 2. Для Confidential Client авторизация передается через Basic Auth с URL-экранированием
			var credentials = $"{Uri.EscapeDataString(cleanClientId)}:{Uri.EscapeDataString(cleanClientSecret)}";
			var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

			// 3. ИСПРАВЛЕНИЕ: В теле запроса НЕ ДОЛЖНО быть "client_id", если используется Basic Auth!
			var values = new Dictionary<string, string>
			{
				{ "grant_type", "refresh_token" },
				{ "refresh_token", refreshToken }
			};

			var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
			{
				Content = new FormUrlEncodedContent(values)
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

			// User-Agent для защиты от блокировок Cloudflare
			request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

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
