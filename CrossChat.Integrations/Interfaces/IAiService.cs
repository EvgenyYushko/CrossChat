using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IAiService
	{
		Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token);
	}
}
