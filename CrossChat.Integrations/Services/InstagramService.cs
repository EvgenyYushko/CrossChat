using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.Extensions.Logging;

namespace CrossChat.Integrations.Services;

public class InstagramService : IInstagramService
{
	private readonly HttpClient _httpClient;
	private readonly ILogger<InstagramService> _logger;

	// Используем актуальную версию API
	private const string ApiVersion = "v21.0";

	public InstagramService(HttpClient httpClient, ILogger<InstagramService> logger)
	{
		_httpClient = httpClient;
		_logger = logger;
	}

	// =================================================================
	// 1. ОТПРАВКА СООБЩЕНИЯ
	// =================================================================
	public async Task SendMessageAsync(string recipientId, string text, string accessToken)
	{
		var url = $"{ApiVersion}/me/messages";

		var payload = new
		{
			recipient = new { id = recipientId },
			message = new { text }
		};

		// Важно: Добавляем токен в заголовок для этого запроса
		_httpClient.DefaultRequestHeaders.Authorization =
			new AuthenticationHeaderValue("Bearer", accessToken);

		var response = await _httpClient.PostAsJsonAsync(url, payload);

		if (response.IsSuccessStatusCode)
		{
			_logger.LogInformation($"[Instagram] ✅ Сообщение отправлено пользователю {recipientId}");
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
	public async Task<List<MessageItem>> GetHistoryAsync(string userId, string accessToken, int limit = 10)
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
			Console.WriteLine($"[System] Показали статус 'печатает' для {recipientId}");
		}
		catch
		{
			// Игнорируем ошибки "печатания", они не критичны
		}
	}
}