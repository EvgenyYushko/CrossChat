using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;

namespace CrossChat.Services
{
    public class VoiceService : IVoiceService
    {
        private readonly IEnumerable<ITtsProvider> _providers;
        private readonly ILogger<VoiceService> _logger;

        public VoiceService(IEnumerable<ITtsProvider> providers, ILogger<VoiceService> logger)
        {
            _providers = providers;
            _logger = logger;
        }

        public async Task<byte[]> GenerateVoiceAudioAsync(VoiceGenerationRequest request, string? providerName = "Gemini")
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                throw new ArgumentException("Текст не может быть пустым", nameof(request));

            string targetProvider = string.IsNullOrWhiteSpace(providerName) ? "Gemini" : providerName;

            var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(targetProvider, StringComparison.OrdinalIgnoreCase))
                           ?? _providers.FirstOrDefault(p => p.ProviderName == "Gemini");

            if (provider == null)
                throw new InvalidOperationException($"Провайдер '{targetProvider}' не зарегистрирован.");

            return await provider.SynthesizeAsync(request);
        }
    }

    // Расширение для контроллеров Web-проекта: вызовы в ProfileController менять не придётся!
    public static class VoiceServiceExtensions
    {
        public static Task<byte[]> GenerateVoiceAudioAsync(
            this IVoiceService voiceService,
            string text,
            ProfileVoiceSetting settings,
            string? profileName = null)
        {
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

            return voiceService.GenerateVoiceAudioAsync(request, settings.Provider);
        }
    }
}