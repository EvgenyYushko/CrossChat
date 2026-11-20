using System.Diagnostics;
using System.Net;
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

		private const int MaxAttempts = 2;
		private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);

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
					},
					wants_link = new
					{
						type = "noul",
						instructions = "Is the latest [User] message asking for a link, URL, Telegram channel/group/username, " +
					   "external website, private page, or asking where to find/buy/subscribe to content " +
					   "(e.g. 'drop the link', 'send your tg', 'дай ссылку', 'скинь тг', 'где тебя найти')?"
					},
					needs_human = new
					{
						type = "noul",
						instructions = "Should this conversation be handed over to a real human owner/manager right now? " +
					   "Answer TRUE if the user explicitly asks for a human/manager/admin, expresses anger or a complaint, " +
					   "reports a payment or order problem, or makes a specific business/collaboration inquiry."
					},
					should_reply_with_voice = new
					{
						type = "noul",
						instructions = "Should the assistant reply with a rare voice note instead of plain text? " +
									   "Answer TRUE ONLY if: (1) the user sent a voice message, OR (2) the user explicitly asks for a voice/audio reply, " +
									   "OR (3) it is a truly special emotional moment where a voice note adds major value — AND the assistant has NOT already sent a voice message ('🎙️') in the recent messages. " +
									   "Answer FALSE for normal flirting, casual chat, factual questions, links, or frequent back-to-back voice notes."
					},
					contains_personal_fact = new
					{
						type = "noul",
						instructions = "Did the latest [User] message(s) reveal any meaningful, lasting personal fact about the user worth remembering long-term " +
						"(such as their real name, city/country, job/occupation, hobbies, pets, relationship status, birthday/upcoming life event, or stable preferences)? " +
						"Answer FALSE for generic small talk, greetings, compliments, questions about the assistant, or temporary reactions."
					}
				}
			};

			Exception? lastException = null;

			for (int attempt = 1; attempt <= MaxAttempts; attempt++)
			{
				using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				attemptCts.CancelAfter(AttemptTimeout);

				try
				{
					using var req = new HttpRequestMessage(HttpMethod.Post, "v1beta/decisions");
					req.Headers.Add("x-goog-api-key", _apiKey);
					req.Content = JsonContent.Create(payload);

					using var resp = await _httpClient.SendAsync(req, attemptCts.Token);

					if (!resp.IsSuccessStatusCode)
					{
						var errBody = await resp.Content.ReadAsStringAsync(attemptCts.Token);
						bool isTransient = (int)resp.StatusCode >= 500 ||
										   resp.StatusCode == HttpStatusCode.RequestTimeout ||
										   resp.StatusCode == HttpStatusCode.TooManyRequests;

						if (isTransient && attempt < MaxAttempts)
						{
							_logger.LogWarning("⚠️ [Gemini Decision 1] Временная ошибка HTTP {Code} на попытке {Attempt}/{Max}. Повторяем...",
								(int)resp.StatusCode, attempt, MaxAttempts);
							await Task.Delay(200, ct);
							continue;
						}

						sw.Stop();
						result.ElapsedMs = sw.ElapsedMilliseconds;
						result.ErrorMessage = $"HTTP {(int)resp.StatusCode}: {errBody}";
						_logger.LogWarning("⚠️ [Gemini Decision 1] Ошибка API: {Err}. Используем Fail-Open (отвечаем обычно).", result.ErrorMessage);
						return result;
					}

					var apiResp = await resp.Content.ReadFromJsonAsync<DecisionsApiResponse>(cancellationToken: attemptCts.Token);
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

						if (apiResp.Answers.TryGetValue("wants_link", out var wl))
						{
							result.WantsLinkProb = wl.Noul ?? 0.0;
							result.WantsLinkConfidence = wl.Confidence ?? 1.0;
						}

						if (apiResp.Answers.TryGetValue("needs_human", out var nh))
						{
							result.NeedsHumanProb = nh.Noul ?? 0.0;
							result.NeedsHumanConfidence = nh.Confidence ?? 1.0;
						}

						if (apiResp.Answers.TryGetValue("should_reply_with_voice", out var sv))
						{
							result.ShouldVoiceReplyProb = sv.Noul ?? 0.0;
							result.ShouldVoiceReplyConfidence = sv.Confidence ?? 1.0;
						}

						if (apiResp.Answers.TryGetValue("contains_personal_fact", out var pf))
						{
							result.ContainsPersonalFactProb = pf.Noul ?? 0.0;
							result.ContainsPersonalFactConfidence = pf.Confidence ?? 1.0;
						}

						result.InputTokens = apiResp.Usage?.TotalInputTokens ?? 0;
						result.IsSuccess = true;
					}

					return result;
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					// Отмена пришла снаружи (остановка приложения / RabbitMQ) — не ретраим
					sw.Stop();
					result.ElapsedMs = sw.ElapsedMilliseconds;
					result.ErrorMessage = "Operation canceled by caller";
					return result;
				}
				catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
				{
					lastException = ex;

					if (attempt < MaxAttempts)
					{
						_logger.LogWarning("⚠️ [Gemini Decision 1] Сетевой сбой/таймаут на попытке {Attempt}/{Max} ({Msg}). Повторяем на новом соединении...",
							attempt, MaxAttempts, ex.Message);
						await Task.Delay(100, ct);
						continue;
					}
				}
				catch (Exception ex)
				{
					lastException = ex;
					break;
				}
			}

			sw.Stop();
			result.ElapsedMs = sw.ElapsedMilliseconds;
			result.ErrorMessage = lastException?.Message ?? "Unknown error";
			_logger.LogWarning(lastException, "⚠️ [Gemini Decision 1] Исключение при вызове после {MaxAttempts} попыток. Используем Fail-Open.", MaxAttempts);

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