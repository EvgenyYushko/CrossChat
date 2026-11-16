using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CrossChat.Integrations.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services
{
    public class GeminiDecisionService : IGeminiDecisionService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GeminiDecisionService> _logger;
        private readonly string _apiKey;

        public GeminiDecisionService(
            HttpClient httpClient,
            IConfiguration config,
            ILogger<GeminiDecisionService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiKey = config["GEMINI_API_KEY"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        }

        public async Task<DirectReplyDecisionResult> EvaluateDirectReplyAsync(string conversationTranscript, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new DirectReplyDecisionResult();

            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(conversationTranscript))
            {
                result.ErrorMessage = "Missing API key or empty transcript";
                return result;
            }

            var payload = new
            {
                model = "gemini-decision-1",
                input = conversationTranscript,
                questions = new
                {
                    needs_reply = new
                    {
                        type = "noul",
                        instructions = "Analyze the latest [User] message in the context of the Instagram Direct conversation. " +
                                       "Does it require a meaningful text reply? " +
                                       "Answer TRUE if the user asks a question, greets, flirts, shares news, makes a request, or invites further conversation. " +
                                       "Answer FALSE if the latest message is ONLY emojis, stickers, laughter (like 'haha', 'лол', 'ахах'), " +
                                       "or a brief closing acknowledgment that ends the turn (like 'ok', 'thanks', 'got it', 'ок', 'спасибо', 'понятно', 'хорошо', 'угу')."
                    },
                    is_spam = new
                    {
                        type = "noul",
                        instructions = "Is the latest [User] message unsolicited commercial spam, crypto/betting/promotion advertising, " +
                                       "suspicious phishing links, mass-follow bot spam, or an automated promotional broadcast?"
                    }
                }
            };

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "v1beta/decisions");
                req.Headers.Add("x-goog-api-key", _apiKey);
                req.Content = JsonContent.Create(payload);

                using var resp = await _httpClient.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync(ct);
                    sw.Stop();
                    result.ElapsedMs = sw.ElapsedMilliseconds;
                    result.ErrorMessage = $"HTTP {(int)resp.StatusCode}: {errBody}";
                    _logger.LogWarning("⚠️ [Gemini Decision 1] Ошибка API: {Err}. Используем Fail-Open (отвечаем обычно).", result.ErrorMessage);
                    return result;
                }

                var apiResp = await resp.Content.ReadFromJsonAsync<DecisionsApiResponse>(cancellationToken: ct);
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;

                if (apiResp?.Answers != null)
                {
                    if (apiResp.Answers.TryGetValue("needs_reply", out var nr))
                    {
                        result.NeedsReplyProb = nr.Noul ?? 1.0;
                        result.NeedsReplyConfidence = nr.Confidence ?? 1.0;
                    }

                    if (apiResp.Answers.TryGetValue("is_spam", out var sp))
                    {
                        result.IsSpamProb = sp.Noul ?? 0.0;
                        result.IsSpamConfidence = sp.Confidence ?? 1.0;
                    }

                    result.InputTokens = apiResp.Usage?.TotalInputTokens ?? 0;
                    result.IsSuccess = true;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                result.ErrorMessage = ex.Message;
                _logger.LogWarning(ex, "⚠️ [Gemini Decision 1] Исключение при вызове. Используем Fail-Open.");
            }

            return result;
        }

        private class DecisionsApiResponse
        {
            [JsonPropertyName("answers")]
            public Dictionary<string, DecisionAnswerItem>? Answers { get; set; }

            [JsonPropertyName("usage")]
            public DecisionUsageInfo? Usage { get; set; }
        }

        private class DecisionAnswerItem
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("noul")]
            public double? Noul { get; set; }

            [JsonPropertyName("confidence")]
            public double? Confidence { get; set; }
        }

        private class DecisionUsageInfo
        {
            [JsonPropertyName("total_input_tokens")]
            public int TotalInputTokens { get; set; }
        }
    }
}