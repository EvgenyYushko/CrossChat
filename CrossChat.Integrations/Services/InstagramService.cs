using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Polly;

namespace CrossChat.Integrations.Services;

public class InstagramService : IInstagramService
{
	private readonly HttpClient _httpClient;
	private readonly ILogger<InstagramService> _logger;
	private readonly IAiService _aiService;

	// Используем актуальную версию API
	private const string ApiVersion = "v21.0";

	public InstagramService(HttpClient httpClient, ILogger<InstagramService> logger, IAiService aiService)
	{
		_httpClient = httpClient;
		_logger = logger;
		_aiService = aiService;
	}

	public async Task<(string NewToken, int ExpiresIn)?> RefreshTokenAsync(string currentToken)
	{
		var url = $"refresh_access_token?grant_type=ig_refresh_token&access_token={currentToken}";

		try
		{
			var response = await _httpClient.GetAsync(url);
			var content = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
			{
				_logger.LogError($"[TokenRefresh] Ошибка обновления токена: {content}");
				return null;
			}

			using var doc = JsonDocument.Parse(content);
			var root = doc.RootElement;

			var newToken = root.GetProperty("access_token").GetString();
			var expiresIn = root.GetProperty("expires_in").GetInt32();

			return (newToken, expiresIn);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "[TokenRefresh] Критическая ошибка запроса.");
			return null;
		}
	}

	public async Task SendMessageAsync(string recipientId, string text, string accessToken)
	{
		var url = $"{ApiVersion}/me/messages";

		var payload = new
		{
			recipient = new { id = recipientId },
			message = new { text }
		};

		// Важно: Добавляем токен в заголовок для этого запроса
		_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

		var response = await _httpClient.PostAsJsonAsync(url, payload);

		if (response.IsSuccessStatusCode)
		{
			//_logger.LogInformation($"[Instagram] ✅ Сообщение отправлено пользователю {recipientId}");
		}
		else
		{
			var errorContent = await response.Content.ReadAsStringAsync();
			_logger.LogError($"[Instagram] ❌ Ошибка отправки: {errorContent}");
			// Можно бросить исключение, чтобы MassTransit попробовал снова
			throw new Exception($"Instagram API Error: {errorContent}");
		}
	}

	// =================================================================
	// 2. ПОЛУЧЕНИЕ ИСТОРИИ (Сборный метод)
	// =================================================================
	public async Task<List<MessageItem>> GetHistoryAsync(string userId, string accessToken, int limit = 20)
	{
		// Шаг А: Узнаем ID диалога (Conversation ID) по ID пользователя
		var conversationId = await GetConversationIdByUserAsync(userId, accessToken);

		if (string.IsNullOrEmpty(conversationId))
		{
			_logger.LogWarning($"[Instagram] Диалог с пользователем {userId} не найден. Возможно, прошло более 24 часов или нет прав.");
			return new List<MessageItem>();
		}

		// Шаг Б: Получаем сообщения этого диалога
		return await GetConversationMessagesAsync(conversationId, accessToken, limit);
	}

	// --- Вспомогательный: Поиск Conversation ID ---
	private async Task<string?> GetConversationIdByUserAsync(string userId, string accessToken)
	{
		// Endpoint: me/conversations?platform=instagram&user_id={USER_ID}
		var url = $"{ApiVersion}/me/conversations?platform=instagram&user_id={userId}&access_token={accessToken}";

		var response = await _httpClient.GetAsync(url);

		if (!response.IsSuccessStatusCode)
		{
			var err = await response.Content.ReadAsStringAsync();
			_logger.LogError($"[Instagram] Ошибка поиска диалога: {err}");
			return null;
		}

		var json = await response.Content.ReadAsStringAsync();
		var data = JsonSerializer.Deserialize<ConversationsResponse>(json);

		// Берем первый найденный диалог
		return data?.Data?.FirstOrDefault()?.Id;
	}

	// --- Вспомогательный: Получение сообщений (Твоя реализация) ---
	private async Task<List<MessageItem>> GetConversationMessagesAsync(string conversationId, string accessToken, int limit)
	{
		// Запрашиваем поля: from (кто писал), message (текст), created_time
		// Можно добавить attachments, если нужно фото
		var fields = $"messages.limit({limit}){{from,message,created_time,is_unsupported}}";
		var url = $"{ApiVersion}/{conversationId}?fields={fields}&access_token={accessToken}";

		var response = await _httpClient.GetAsync(url);

		if (!response.IsSuccessStatusCode)
		{
			_logger.LogError($"[Instagram] Ошибка получения сообщений для {conversationId}");
			return new List<MessageItem>();
		}

		var json = await response.Content.ReadAsStringAsync();

		// Используем твои модели
		var convoData = JsonSerializer.Deserialize<ConversationMessagesResponse>(json);

		return convoData?.Messages?.Data ?? new List<MessageItem>();
	}

	public async Task SetTypingStatusAsync(string recipientId, string accessToken, bool on = true)
	{
		var url = $"{ApiVersion}/me/messages?access_token={accessToken}";

		var payload = new
		{
			recipient = new { id = recipientId },
			sender_action = on ? "typing_on" : "typing_off"
		};

		var json = JsonSerializer.Serialize(payload);
		var content = new StringContent(json, Encoding.UTF8, "application/json");

		try
		{
			// Мы не ждем ответа (fire and forget), чтобы не тормозить основной поток,
			// или можно ждать, если критично. Обычно ошибки тут не важны.
			await _httpClient.PostAsync(url, content);
			//Console.WriteLine($"[System] Показали статус 'печатает' для {recipientId}");
		}
		catch
		{
			// Игнорируем ошибки "печатания", они не критичны
		}
	}

	public string GetRandomReaction(string allowedReactions)
	{
		if (string.IsNullOrWhiteSpace(allowedReactions))
			return "👍";

		var reactions = allowedReactions.Split(',', StringSplitOptions.RemoveEmptyEntries);

		var random = new Random();
		return reactions[random.Next(reactions.Length)];
	}

	public async Task<bool> SendReactionAsync(string recipientId, string messageId, string reaction, string accessToken)
	{
		var url = $"{ApiVersion}/me/messages?access_token={accessToken}";

		var payload = new
		{
			recipient = new { id = recipientId },
			sender_action = "react",
			payload = new
			{
				message_id = messageId,
				reaction = reaction
			}
		};

		var json = JsonSerializer.Serialize(payload);
		var content = new StringContent(json, Encoding.UTF8, "application/json");

		try
		{
			var response = await _httpClient.PostAsync(url, content);

			if (response.IsSuccessStatusCode)
			{
				return true;
			}

			var error = await response.Content.ReadAsStringAsync();
			Console.WriteLine($"[Reaction Error] Не удалось отправить реакцию: {error}");
			return false;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Reaction Error] Ошибка: {ex.Message}");
			return false;
		}
	}

	public async Task<(string? username, string? instagramScopedUserId, string? profilePicUrl)> GetMeInfo(string? accessToken)
	{
		var userUrl = $"https://graph.instagram.com/me?fields=id,user_id,username,profile_picture_url&access_token={accessToken}";
		var userResponse = await _httpClient.GetAsync(userUrl);

		var username = "Unknown";
		var instagramScopedUserId = ""; // Это user_id (для Deauth)
		var profilePicUrl = "";         // Ссылка на фото

		if (userResponse.IsSuccessStatusCode)
		{
			using var userDoc = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
			var root = userDoc.RootElement;

			if (root.TryGetProperty("username", out var u)) username = u.GetString();
			if (root.TryGetProperty("profile_picture_url", out var p)) profilePicUrl = p.GetString();
			if (root.TryGetProperty("user_id", out var i)) instagramScopedUserId = i.GetString();
		}

		return (username, instagramScopedUserId, profilePicUrl);
	}

	public async Task<InstagramUserProfile> GetInstagramUserProfileAsync(string userId, string accessToken)
	{
		var fields = "name,username,profile_pic,is_verified_user,follower_count,is_user_follow_business,is_business_follow_user";
		var url = $"v19.0/{userId}?fields={fields}&access_token={accessToken}";

		var response = await _httpClient.GetAsync(url);
		if (!response.IsSuccessStatusCode) return null;

		var json = await response.Content.ReadAsStringAsync();
		return JsonSerializer.Deserialize<InstagramUserProfile>(json);
	}

	public async Task ReplyToCommentAsync(string commentId, string text, string accessToken)
	{
		var url = $"{ApiVersion}/{commentId}/replies?access_token={accessToken}";

		var payload = new { message = text };
		var response = await _httpClient.PostAsJsonAsync(url, payload);

		if (response.IsSuccessStatusCode)
		{
			_logger.LogInformation($"[Instagram] ✅ Ответ на комментарий {commentId} отправлен.");
		}
		else
		{
			var errorContent = await response.Content.ReadAsStringAsync();
			_logger.LogError($"[Instagram] ❌ Ошибка ответа на коммент: {errorContent}");
			throw new Exception($"Instagram API Error: {errorContent}");
		}
	}

	public static ConcurrentDictionary<string, string> ContextCache = new();

	public async Task<string> GetUserContextForAiAsync(string userId, string accessToken)
	{
		// 1. Проверка КЭША
		if (ContextCache.TryGetValue(userId, out string cachedContext))
		{
			_logger.LogInformation($"Взяли текст для userId: {userId} из кеша");
			return cachedContext;
		}

		try
		{
			// 2. Запрос к API Instagram
			var userProfile = await GetInstagramUserProfileAsync(userId, accessToken);
			if (userProfile == null) return "";

			// 3. Анализ внешности (Vision)
			string appearanceDescription = "The profile photo is missing.";
			if (!string.IsNullOrEmpty(userProfile.ProfilePicUrl))
			{
				try
				{
					// Скачиваем фото в байты/base64 (используем ваш существующий метод)
					var base64Image = await DownloadImageAsBase64(userProfile.ProfilePicUrl);
					appearanceDescription = await AnalyzeImageAsync(base64Image, "photo", null);
				}
				catch (Exception ex)
				{
					_logger.LogInformation($"[Profile Vision Error]: {ex.Message}");
					appearanceDescription = "Failed to upload profile photo.";
				}
			}

			// 4. Формирование текста контекста
			var sb = new StringBuilder();
			sb.AppendLine("INFORMATION ABOUT THE INTERLOCUTOR:");
			sb.AppendLine($"Name: {userProfile.Name ?? "Not specified"}");
			sb.AppendLine($"Nickname: @{userProfile.Username}");
			sb.AppendLine($"Subscribers: {userProfile.FollowerCount}");
			sb.AppendLine($"Subscribed to you: {(userProfile.IsFollowingMe ? "Yes" : "No")}");
			sb.AppendLine($"Are you subscribed to it: {(userProfile.IsFollowingYou ? "Yes" : "No")}");
			sb.AppendLine($"Verification check mark: {(userProfile.IsVerified ? "Yes" : "No")}");
			sb.AppendLine($"Appearance (based on profile photo): {appearanceDescription}");

			string finalContext = sb.ToString();

			// 5. Сохранение в кэш
			ContextCache.TryAdd(userId, finalContext);

			_logger.LogInformation($"[User Profile] Сформирован контекст для {userProfile.Username}");
			return finalContext;
		}
		catch (Exception ex)
		{
			_logger.LogError($"Ошибка получения профиля: {ex.Message}");
			return "";
		}
	}

	private async Task<string> AnalyzeImageAsync(string base64Image, string type, string aiToken)
	{
		if (string.IsNullOrEmpty(base64Image))
		{
			return "";
		}

		_logger.LogInformation($"Starting {type} analysis");

		var prompt = $"Analyze what is depicted on this {type} and give a description 2-3 sentences. " +
					"Response format: only the response text, no quotes or formatting.";

		_logger.LogInformation($"Calling Gemini with base64 {type} (length: {base64Image?.Length ?? 0})");

		string responseText = "";
		try
		{
			if (type == "video")
			{
				responseText = await _aiService.GeminiRequestWithVideo(prompt, base64Image, aiToken);
			}
			else
			{
				responseText = await _aiService.GeminiRequestWithImage(prompt, base64Image, aiToken);
			}
			_logger.LogInformation($"Gemini response received: {responseText?.Substring(0, Math.Min(50, responseText.Length))}...");
		}
		catch (RpcException ex)
		{
			// Проверяем, заблокировал ли Google контент
			if (ex.Status.Detail.Contains("blocked"))
			{
				_logger.LogWarning($"[Safety Filter] Gemini заблокировал контент: {ex.Status.Detail}");
				return "NSFW content";
			}

			// Если это другая ошибка gRPC
			_logger.LogError(ex, "gRPC ошибка при анализе медиа");
			return "";
		}
		catch (Exception geminiEx)
		{
			_logger.LogError(geminiEx, $"Gemini API error");
		}

		return responseText ?? "";
	}

	private async Task<string> ProcessAudioMessage(string audioBase64)
	{
		try
		{
			_logger.LogInformation($"Audio message received");

			var audioText = await _aiService.GeminiAudioToText(audioBase64, null);
			Console.WriteLine("Распознонное голосовое: " + audioText);
			return audioText;
			//await SendMessageWithHistory(audioText, senderId);
		}
		catch (Exception ex)
		{
			_logger.LogInformation(ex, $"Error processing audio");
		}

		return "";
	}

	public async Task<string> ProcessAndCacheMediaAsync(MediaDataEntry media, string messageId)
	{
		string resultText = "";
		try
		{
			switch (media.MediaType)
			{
				case "audio":
					{
						var base64 = await DownloadAudioFileAsBase64(media.Url);
						resultText = $"[voice message]: {await ProcessAudioMessage(base64)}";
					}
					break;
				case "image":
					{
						var base64 = await DownloadImageAsBase64(media.Url);
						resultText = $"[Photo]: {await AnalyzeImageAsync(base64, "photo", null)}";
					}
					break;
				case "video":
				case "ig_reel":
					{
						var base64 = await DownloadImageAsBase64(media.Url);
						resultText = $"[Video]: {await AnalyzeImageAsync(base64, "video", null)}";
					}
					break;
				default:
					resultText = $"[Медиа: {media.MediaType}]";
					break;
			}

			// Записываем результат в сам объект
			media.AiResult = resultText;
			media.IsProcessed = true;

			// Если это объект из КЭША, он там уже лежит по ссылке, изменения отразятся сразу.
			// Если это новый объект из API, вызывающий код сам добавит его в словарь.
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Ошибка обработки медиа ({media.MediaType}): {ex.Message}");
			resultText = $"[Failed to process {media.MediaType}]";
		}

		return resultText;
	}

	private async Task<string> DownloadImageAsBase64(string imageUrl)
	{
		// 1. Создаем политику повторов: 3 попытки, если Инста вернула 404 или упала сеть
		// Паузы: 1 сек, 2 сек, 4 сек. Это даст время CDN Фейсбука обновить кэш.
		var retryPolicy = Policy
			.Handle<HttpRequestException>() // Ловим 404, 403, 500 и обрывы сети
			.Or<TaskCanceledException>()    // Ловим таймауты
			.WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)),
				(exception, timeSpan, retryCount, context) =>
				{
					_logger.LogWarning($"[Download Image] Попытка {retryCount} провалилась: {exception.Message}. Ждем {timeSpan.TotalSeconds} сек...");
				});

		try
		{
			// 2. Оборачиваем скачивание в retryPolicy
			return await retryPolicy.ExecuteAsync(async () =>
			{
				using var httpClient = new HttpClient();

				// Делаем запрос более похожим на настоящий браузер
				httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
				httpClient.DefaultRequestHeaders.Add("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
				httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

				var imageBytes = await httpClient.GetByteArrayAsync(imageUrl);

				if (imageBytes == null || imageBytes.Length == 0)
				{
					throw new HttpRequestException("Скачался пустой файл");
				}

				return Convert.ToBase64String(imageBytes);
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"[Download Image] Критическая ошибка при скачивании после всех попыток: {imageUrl}");
			return null;
		}
	}

	private async Task<string> DownloadAudioFileAsBase64(string audioUrl)
	{
		try
		{
			using var httpClient = new HttpClient();
			// Добавляем заголовки для успешного скачивания
			httpClient.DefaultRequestHeaders.Add("User-Agent",
				"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

			var response = await httpClient.GetAsync(audioUrl);
			if (response.IsSuccessStatusCode)
			{
				var audioBytes = await response.Content.ReadAsByteArrayAsync();

				// Конвертируем в base64 строку
				var base64String = Convert.ToBase64String(audioBytes);

				_logger.LogInformation($"Audio converted to base64, length: {base64String.Length} chars");
				return base64String;
			}

			_logger.LogInformation($"Failed to download audio: {response.StatusCode}");
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error downloading audio file");
			return null;
		}
	}
}