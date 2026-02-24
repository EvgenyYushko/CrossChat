using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static System.Net.WebRequestMethods;

namespace Instargam
{
	public class InstagramService
	{
		private readonly HttpClient _https;
		private readonly ILogger<InstagramService> _logger;

		public InstagramService(ILogger<InstagramService> logger)
		{
			_https = new HttpClient { BaseAddress = new Uri("https://graph.instagram.com/") };
			_logger = logger;
		}

		public async Task SendInstagramMessage(string recipientId, string text, string accessToken)
		{
			var url = $"v19.0/me/messages";

			var payload = new
			{
				recipient = new { id = recipientId },
				message = new { text }
			};

			var json = JsonSerializer.Serialize(payload);
			var content = new StringContent(json, Encoding.UTF8, "application/json");

			_https.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

			var response = await _https.PostAsync(url, content);

			if (response.IsSuccessStatusCode)
			{
				_logger.LogInformation("Message sent successfully");
			}
			else
			{
				var error = await response.Content.ReadAsStringAsync();
				_logger.LogError($"Failed to send message: {error}");
			}
		}
	}
}
