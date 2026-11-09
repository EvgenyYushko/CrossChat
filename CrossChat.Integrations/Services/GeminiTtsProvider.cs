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

            var httpOptions = new HttpOptions { ApiVersion = "v1beta" };
            var client = new Client(apiKey: _apiKey, httpOptions: httpOptions);

            // 🚀 СОБИРАЕМ ТОЧНЫЙ СТИЛЬ КАК В GOOGLE AI STUDIO:
            // Если выбран Whisper -> "whisper quietly"
            string baseStyle = (request.Style?.ToLower()) switch
            {
                "whisper" => "whisper quietly",
                "friendly" => "Warm and neutral delivery with micro-pauses",
                "narration" => "commanding, rich vocal modulation, dramatic pauses",
                "promote" => "High energy, elongated vowels",
                "podcast" => "Calm, measured delivery",
                "intimate" => "intimate, soft and gentle breathing",
                "empathetic" => "Empathetic, caring and warm",
                "energetic" => "Energetic, dynamic and lively",
                "gravelly" => "Gravelly, deep textured tone",
                "smooth" => "Smooth, velvety delivery",
                "soft" => "Soft, tender and quiet",
                "resonant" => "Resonant, deep and full",
                "firm" => "Firm, confident and direct",
                _ => request.Style ?? "whisper quietly"
            };

            // Если пользователь дописал что-то в режиссерскую сцену — объединяем:
            string finalStyle = !string.IsNullOrWhiteSpace(request.Scene)
                ? $"{baseStyle}, {request.Scene.Trim()}"
                : baseStyle;

            _logger.LogInformation("🎙️ [Gemini 3.8] Генерация: Голос={Voice}, Стиль='{Style}', Temp={Temp}", 
                request.VoiceName, finalStyle, request.Temperature);

            // 🚀 ПЕРЕДАЕМ СТИЛЬ В SPEECH METADATA (ТОЧНО КАК В GOOGLE AI STUDIO!)
            var part = new Part
            {
                Text = request.Text.Trim(),
                SpeechMetadata = new SpeechMetadata
                {
                    Style = finalStyle
                }
            };

            var contents = new List<Content>
            {
                new Content
                {
                    Role = "user",
                    Parts = new List<Part> { part }
                }
            };

            var config = new GenerateContentConfig
            {
                HttpOptions = httpOptions,
                Temperature = (float)request.Temperature,
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

            var audioChunks = new List<byte>();

            try
            {
                await foreach (var chunk in client.Models.GenerateContentStreamAsync(_modelName, contents, config))
                {
                    if (chunk.Candidates == null || chunk.Candidates.Count == 0 || chunk.Candidates[0].Content?.Parts == null)
                        continue;

                    foreach (var p in chunk.Candidates[0].Content.Parts)
                    {
                        if (p.InlineData?.Data != null)
                        {
                            audioChunks.AddRange(p.InlineData.Data);
                        }
                    }
                }

                if (audioChunks.Count == 0)
                    throw new Exception("Gemini 3.8 не вернул аудио.");

                return audioChunks.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Ошибка генерации голоса");
                throw;
            }
        }
    }
}