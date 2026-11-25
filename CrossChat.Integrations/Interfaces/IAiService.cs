using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IAiService
	{
		Task<string> GeminiRequest(string prompt, string token);

		Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token);

		Task<string> GeminiRequestWithImage(string prompt, string base64Image, string token);

		Task<string> GeminiRequestWithVideo(string prompt, string base64video, string token);

		Task<string> GeminiAudioToText(string base64Iaudio, string token);

		Task<AiGenerationResult> GeminiRequestWithUsageAsync(string prompt, string? token = null);
		Task<AiGenerationResult> GetAnswerWithUsageAsync(string systemPrompt, List<AiRequest> messages, string? token = null);
		Task<AiGenerationResult> GeminiRequestWithImageWithUsageAsync(string prompt, string base64Image, string? token = null);
		Task<AiGenerationResult> GeminiRequestWithVideoWithUsageAsync(string prompt, string base64video, string? token = null);
		Task<AiGenerationResult> GeminiAudioToTextWithUsageAsync(string base64Audio, string? token = null);

	}
}
