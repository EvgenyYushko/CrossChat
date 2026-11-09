using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;

namespace CrossChat.Services
{
    public interface IVoiceService
    {
        Task<byte[]> GenerateVoiceAudioAsync(string text, ProfileVoiceSetting settings, string? profileName = null);
    }

    public class VoiceService : IVoiceService
    {
        private readonly IEnumerable<ITtsProvider> _providers;
        private readonly ILogger<VoiceService> _logger;

        public VoiceService(IEnumerable<ITtsProvider> providers, ILogger<VoiceService> logger)
        {
            _providers = providers;
            _logger = logger;
        }

        public async Task<byte[]> GenerateVoiceAudioAsync(string text, ProfileVoiceSetting settings, string? profileName = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Текст не может быть пустым", nameof(text));

            string targetProvider = settings.Provider ?? "Gemini";

            var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(targetProvider, StringComparison.OrdinalIgnoreCase))
                           ?? _providers.FirstOrDefault(p => p.ProviderName == "Gemini");

            if (provider == null)
                throw new InvalidOperationException($"Провайдер '{targetProvider}' не зарегистрирован.");

            // Маппим сущность БД в независимый запрос для интеграции:
            var request = new VoiceGenerationRequest
            {
                Text = text,
                ProfileName = profileName ?? settings.Profile?.Name ?? "Creator",
                VoiceName = settings.VoiceName,
                Style = settings.Style,
                Pace = settings.Pace,
                Accent = settings.Accent,
                Scene = settings.Scene,
                SampleContext = settings.SampleContext,
                Temperature = settings.Temperature
            };

            return await provider.SynthesizeAsync(request);
        }
    }
}