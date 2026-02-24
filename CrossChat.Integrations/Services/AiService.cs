using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Grpc.Core;
using Protos.GoogleGeminiService;

namespace CrossChat.Integrations.Services
{
	public class AiService : IAiService
	{
		private readonly GeminiService.GeminiServiceClient _geminiServiceClient;
		private readonly string _token;

		public AiService(GeminiService.GeminiServiceClient geminiServiceClient, string token)
		{
			_geminiServiceClient = geminiServiceClient;
			_token = token;
		}

		public async Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token)
		{
			if(token is null)
			{
				token = _token;
			}

			var rsponce = await _geminiServiceClient.RequestWithChatAsync(new()
			{
				SystemInstruction = systemPrompt,
				History =
				{
					messages.Select(m => new ChatMessage
					{
						Role = m.Role,
						Text = m.Text
					})
				}
			}, AddTokenToHeaders(token));

			return rsponce.GeneratedText;
		}

		private Metadata AddTokenToHeaders(string token)
		{
			var headers = new Metadata();
			headers.Add("x-goog-api-key", token);
			return headers;
		}
	}
}
