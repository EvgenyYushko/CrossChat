using System.Net.Http.Headers;
using System.Net.Http.Json;
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
	}
}
