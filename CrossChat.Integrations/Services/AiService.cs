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

		public async Task<string> GeminiRequest(string prompt, string token)
		{
			if (token is null)
			{
				token = _token;
			}

			var response = await _geminiServiceClient.RequestAsync(new Prompt { Text = prompt }, AddTokenToHeaders(token));
			return response.GeneratedText;
		}

		public async Task<string> GetAnswerAsync(string systemPrompt, List<AiRequest> messages, string token)
		{
			if (token is null)
			{
				token = _token;
			}

			var rsponce = await _geminiServiceClient.RequestWithChatAsync(new()
			{
				SystemInstruction = systemPrompt,
				History =
				{
					messages.Select(m => new ChatMessage
					{
						Role = m.Role,
						Text = m.Text
					})
				}
			}, AddTokenToHeaders(token));

			return rsponce.GeneratedText;
		}

		public async Task<string> GeminiRequestWithImage(string prompt, string base64Image, string token)
		{
			if (token is null)
			{
				token = _token;
			}

			var response = await _geminiServiceClient.RequestWithImageAsync(new()
			{
				Prompt = { new Prompt { Text = prompt } },
				Base64Idata = base64Image
			}, AddTokenToHeaders(token));
			return response.GeneratedText;
		}

		public async Task<string> GeminiRequestWithVideo(string prompt, string base64video, string token)
		{
			if (token is null)
			{
				token = _token;
			}

			var response = await _geminiServiceClient.RequestWithVideoAsync(new()
			{
				Prompt = { new Prompt { Text = prompt } },
				Base64Idata = base64video
			}, AddTokenToHeaders(token));
			return response.GeneratedText;
		}

		public async Task<string> GeminiAudioToText(string base64Iaudio, string token)
		{
			if (token is null)
			{
				token = _token;
			}

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
				Base64Idata = base64Iaudio
			}, AddTokenToHeaders(token));

			var result = response.GeneratedText ?? "";
			return result.Trim().Trim('"', '«', '»', '`');
		}

		private Metadata AddTokenToHeaders(string token)
		{
			var headers = new Metadata();
			headers.Add("x-goog-api-key", token);
			return headers;
		}
	}
}
