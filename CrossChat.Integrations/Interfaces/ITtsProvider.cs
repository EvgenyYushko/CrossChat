using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	/// <summary>
	/// Интерфейс конкретного голосового провайдера (Gemini, ElevenLabs и т.д.)
	/// </summary>
	public interface ITtsProvider
	{
		string ProviderName { get; } // "Gemini", "ElevenLabs"
		Task<byte[]> SynthesizeAsync(VoiceGenerationRequest request);
	}
}
