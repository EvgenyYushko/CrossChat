using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces;

public interface IVoiceService
{
	Task<byte[]> GenerateVoiceAudioAsync(VoiceGenerationRequest request, string? providerName = "Gemini");
}


