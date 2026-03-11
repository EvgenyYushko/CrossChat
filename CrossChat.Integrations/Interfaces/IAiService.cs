using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IAiService
	{
		Task<string> GeminiRequest(string prompt, string token);

		Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token);

		Task<string> GeminiRequestWithImage(string prompt, string base64Image, string token);

		Task<string> GeminiRequestWithVideo(string prompt, string base64video, string token);
	}
}
