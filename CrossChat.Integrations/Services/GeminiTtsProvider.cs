using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services
{
    public class GeminiTtsProvider : ITtsProvider
    {
        private readonly string _apiKey;
        private readonly ILogger<GeminiTtsProvider> _logger;
        private readonly string _modelName;

        public string ProviderName => "Gemini";

        public GeminiTtsProvider(IConfiguration config, ILogger<GeminiTtsProvider> logger)
        {
            _logger = logger;
            _apiKey = config["GEMINI_API_KEY"] ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            _modelName = config["Gemini:TtsModel"] ?? "gemini-3.8-flash-tts";
        }

        public async Task<byte[]> SynthesizeAsync(VoiceGenerationRequest request)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("API ключ GEMINI_API_KEY не настроен на сервере.");
            }

            // 🚀 КЛЮЧЕВОЙ МОМЕНТ 1: явно переключаем SDK на v1beta, где живет speechConfig!
            var httpOptions = new HttpOptions { ApiVersion = "v1beta" };

            var client = new Client(
                apiKey: _apiKey,
                httpOptions: httpOptions
            );

            // 🚀 КЛЮЧЕВОЙ МОМЕНТ 2: для Gemini 3.8 текст скрипта передаем через ## Transcript:
            // (ровно как в сниппете из твоего Google AI Studio)
            var cleanText = request.Text.Trim();
            var transcriptText = cleanText.StartsWith("## Transcript:", StringComparison.OrdinalIgnoreCase)
                ? cleanText
                : $"## Transcript:\n{cleanText}";

            var contents = new List<Content>
            {
                new Content
                {
                    Role = "user",
                    Parts = new List<Part>
                    {
                        new Part { Text = transcriptText }
                    }
                }
            };

            var config = new GenerateContentConfig
            {
                // Дублируем v1beta в конфигурацию запроса для 100% гарантии
                HttpOptions = httpOptions,
                Temperature = 1,
                ResponseModalities = new List<string> { "audio" },
                SpeechConfig = new SpeechConfig
                {
                    VoiceConfig = new VoiceConfig
                    {
                        PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                        {
                            VoiceName = string.IsNullOrWhiteSpace(request.VoiceName) ? "Tavi" : request.VoiceName
                        }
                    }
                }
            };

            _logger.LogInformation("🎙️ [Gemini 3.8 TTS] Отправка запроса в v1beta (Голос: {Voice}, Модель: {Model})...", 
                request.VoiceName, _modelName);

            var audioChunks = new List<byte>();

            try
            {
                await foreach (var chunk in client.Models.GenerateContentStreamAsync(_modelName, contents, config))
                {
                    if (chunk.Candidates == null || chunk.Candidates.Count == 0 || chunk.Candidates[0].Content?.Parts == null)
                        continue;

                    // Сканируем все части ответа на наличие аудио
                    foreach (var part in chunk.Candidates[0].Content.Parts)
                    {
                        if (part.InlineData?.Data != null)
                        {
                            audioChunks.AddRange(part.InlineData.Data);
                        }
                    }
                }

                if (audioChunks.Count == 0)
                {
                    throw new Exception("Gemini 3.8 не вернул аудиоданные.");
                }

                _logger.LogInformation("✅ [Gemini 3.8 TTS] Аудио успешно сгенерировано! Вес: {Size} байт", audioChunks.Count);
                return audioChunks.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [Gemini 3.8 TTS] Ошибка генерации голоса через модель {Model}", _modelName);
                throw;
            }
        }
    }
}