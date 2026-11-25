using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Grpc.Core;
using Protos.GoogleGeminiService;

namespace CrossChat.Integrations.Services
{
	public class AiService : IAiService
	{
		private readonly GeminiService.GeminiServiceClient _geminiServiceClient;
		private readonly string _token;

		public AiService(GeminiService.GeminiServiceClient geminiServiceClient, string token)
		{
			_geminiServiceClient = geminiServiceClient;
			_token = token;
		}


		

		private Metadata AddTokenToHeaders(string token)
		{
			var headers = new Metadata();
			headers.Add("x-goog-api-key", token);
			return headers;
		}


		private static AiGenerationResult ToUsageResult(GeminiResponse r, string? overrideText = null) => new()
		{
			Text = overrideText ?? r.GeneratedText ?? string.Empty,
			InputTokens = r.InputTokens,
			OutputTokens = r.OutputTokens,
			ThoughtTokens = r.ThoughtTokens,
			TotalTokens = r.TotalTokens > 0 ? r.TotalTokens : (r.InputTokens + r.OutputTokens + r.ThoughtTokens),
			ModelName = r.ModelName ?? string.Empty
		};

		public async Task<AiGenerationResult> GeminiRequestWithUsageAsync(string prompt, string? token = null)
		{
			var response = await _geminiServiceClient.RequestAsync(new Prompt { Text = prompt }, AddTokenToHeaders(token ?? _token));
			return ToUsageResult(response);
		}

		public async Task<string> GeminiRequest(string prompt, string token)
			=> (await GeminiRequestWithUsageAsync(prompt, token)).Text;

		public async Task<AiGenerationResult> GetAnswerWithUsageAsync(string systemPrompt, List<AiRequest> messages, string? token = null)
		{
			var response = await _geminiServiceClient.RequestWithChatAsync(new()
			{
				SystemInstruction = systemPrompt,
				History = { messages.Select(m => new ChatMessage { Role = m.Role, Text = m.Text }) }
			}, AddTokenToHeaders(token ?? _token));

			return ToUsageResult(response);
		}

		public async Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token)
			=> (await GetAnswerWithUsageAsync(systemPrompt, messages, token)).Text;

		public async Task<AiGenerationResult> GeminiRequestWithImageWithUsageAsync(string prompt, string base64Image, string? token = null)
		{
			var response = await _geminiServiceClient.RequestWithImageAsync(new()
			{
				Prompt = { new Prompt { Text = prompt } },
				Base64Idata = base64Image
			}, AddTokenToHeaders(token ?? _token));
			return ToUsageResult(response);
		}

		public async Task<string> GeminiRequestWithImage(string prompt, string base64Image, string token)
			=> (await GeminiRequestWithImageWithUsageAsync(prompt, base64Image, token)).Text;

		public async Task<AiGenerationResult> GeminiRequestWithVideoWithUsageAsync(string prompt, string base64video, string? token = null)
		{
			var response = await _geminiServiceClient.RequestWithVideoAsync(new()
			{
				Prompt = { new Prompt { Text = prompt } },
				Base64Idata = base64video
			}, AddTokenToHeaders(token ?? _token));
			return ToUsageResult(response);
		}

		public async Task<string> GeminiRequestWithVideo(string prompt, string base64video, string token)
			=> (await GeminiRequestWithVideoWithUsageAsync(prompt, base64video, token)).Text;

		public async Task<AiGenerationResult> GeminiAudioToTextWithUsageAsync(string base64Audio, string? token = null)
		{
			// 🚀 ПРОКАЧАННЫЙ РЕЖИССЁРСКИЙ ПРОМПТ ДЛЯ РАСПОЗНАВАНИЯ ЭМОЦИЙ И АРТЕФАКТОВ ГОЛОСА
			var prompt = @"Сделай точную дословную транскрибацию этого аудиофайла на языке оригинала.
				Твоя ключевая задача — уловить не только произнесённые слова, но и живой эмоциональный фон, невербальные звуки и дыхание.

				Вставляй специальные теги в угловых скобках <tag> ровно в тех местах, где они звучат в речи:
				- Смех / усмешка / хихиканье: <chuckle>, <laugh>, <giggle>
				- Дыхание / вздохи: <sigh>, <breath>, <heavy breath>, <gasp>
				- Интимность / шёпот: <whisper>
				- Эмоциональные звуки: <groan>, <whimper>, <yawn>
				- Прочистка горла / кашель: <throat-clearing>, <cough>
				- Заметные паузы: <short pause>, <long pause>

				Пример правильного вывода:
				""Привет! <chuckle> Ты не поверишь, что произошло. <sigh> Я так устала сегодня...""

				Правила:
				1. Выведи ТОЛЬКО готовый текст с тегами.
				2. Не добавляй никаких пояснений, комментариев от себя и кавычек.
				3. Сохраняй естественную пунктуацию.";

			var response = await _geminiServiceClient.RequestWithAudioAsync(new()
			{
				Prompt = { new Prompt { Text = prompt } },
				Base64Idata = base64Audio
			}, AddTokenToHeaders(token ?? _token));

			var clean = (response.GeneratedText ?? "").Trim().Trim('"', '«', '»', '`');
			return ToUsageResult(response, clean);
		}

		public async Task<string> GeminiAudioToText(string base64Iaudio, string token)
			=> (await GeminiAudioToTextWithUsageAsync(base64Iaudio, token)).Text;
	}
}
