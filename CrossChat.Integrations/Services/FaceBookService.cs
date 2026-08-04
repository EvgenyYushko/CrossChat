using System.Text.Json;
using CrossChat.Integrations.Interfaces;

namespace CrossChat.Integrations.Services
{
	public partial class FaceBookService : IFaceBookService
	{
		public async Task<FbUser> GetMeAsync(string token)
		{
			string url = $"https://graph.facebook.com/v24.0/me" +
						 $"?fields=name,id,picture" +
						 $"&access_token={token}";

			using (var httpClient = new HttpClient())
			{
				var response = await httpClient.GetAsync(url);

				if (response.IsSuccessStatusCode)
				{
					var json = await response.Content.ReadAsStringAsync();
					using var userDoc = JsonDocument.Parse(json);
					var root = userDoc.RootElement;

					var fbUser = new FbUser();

					// Получаем name
					if (root.TryGetProperty("name", out var nameElement))
						fbUser.Name = nameElement.GetString() ?? "Unknown";

					// Получаем id
					if (root.TryGetProperty("id", out var idElement))
						fbUser.Id = idElement.GetString() ?? "";

					// Получаем picture.url (вложенная структура)
					if (root.TryGetProperty("picture", out var pictureElement) &&
						pictureElement.TryGetProperty("data", out var dataElement) &&
						dataElement.TryGetProperty("url", out var urlElement))
					{
						fbUser.ProfilePicUrl = urlElement.GetString() ?? "";
					}

					return fbUser;
				}

				return new FbUser { Name = "Unknown", Id = "", ProfilePicUrl = "" };
			}
		}

		/// <summary>
		/// Получает последние сообщения от пользователей, на которые мы еще не ответили
		/// </summary>
		public async Task<List<FbConversation>> GetUnreadDialogsAsync(string token, string pageId)
		{
			// 2. Формируем запрос
			// Мы просим список диалогов, где unread_count > 0
			// И берем последнее сообщение из каждого диалога, чтобы понять, кто писал последним
			string url = $"https://graph.facebook.com/v24.0/me/conversations" +
						 $"?fields=id,unread_count,messages.limit(1){{from,message}}" +
						 $"&access_token={token}";

			var unreadConversation = new List<FbConversation>();

			using (var httpClient = new HttpClient())
			{
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode)
				{
					string error = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"Ошибка получения диалогов FB: {error}");
					return unreadConversation;
				}

				var json = await response.Content.ReadAsStringAsync();
				var conversationData = JsonSerializer.Deserialize<FbConversationResponse>(json);

				if (conversationData?.data == null) return unreadConversation;

				foreach (var convo in conversationData.data/*.Where(c => c.unread_count > 0)*/)
				{
					// Нас интересуют диалоги, где есть непрочитанные сообщения
					// ИЛИ (для надежности) где последнее сообщение написано НЕ нами (НЕ страницей)

					// Пропускаем пустые диалоги
					if (convo.messages?.data == null || !convo.messages.data.Any()) continue;

					var lastMsg = convo.messages.data.First();

					// Проверка: ID отправителя последнего сообщения НЕ должен совпадать с ID нашей страницы
					// (Иначе бот будет бесконечно отвечать сам себе)
					if (lastMsg.from.id != pageId)
					{
						unreadConversation.Add(convo);
					}
				}
			}

			return unreadConversation;
		}

		public async Task<FbConversation> GetDialogByIdAsync(string token, string dlgId)
		{
			string url = $"https://graph.facebook.com/v24.0/{dlgId}" +
						 $"?fields=id,unread_count,messages.limit(10){{from,message}}" +
						 $"&access_token={token}";

			var conversationData = new FbConversation();

			using (var httpClient = new HttpClient())
			{
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode)
				{
					string error = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"Ошибка получения диалога FB: {error}");
					return conversationData;
				}

				var json = await response.Content.ReadAsStringAsync();
				conversationData = JsonSerializer.Deserialize<FbConversation>(json);
			}

			return conversationData;
		}

		/// <summary>
		/// Отправляет ответ пользователю
		/// </summary>
		/// <param name="recipientId">ID пользователя (PSID - Page Scoped ID)</param>
		/// <param name="text">Текст ответа</param>
		public async Task<bool> SendReplyAsync(string recipientId, string text, string token)
		{
			string url = $"https://graph.facebook.com/v24.0/me/messages";

			// Формат запроса для отправки текста
			var payload = new
			{
				recipient = new { id = recipientId },
				message = new { text = text },
				messaging_type = "RESPONSE", // Важно указать, что это ответ
				access_token = token
			};

			using (var httpClient = new HttpClient())
			{
				var jsonPayload = JsonSerializer.Serialize(payload);
				var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

				var response = await httpClient.PostAsync(url, content);

				if (response.IsSuccessStatusCode)
				{
					Console.WriteLine($"✅ FB: Ответ отправлен пользователю {recipientId}");
					return true;
				}
				else
				{
					string error = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"❌ FB: Ошибка отправки сообщения: {error}");
					return false;
				}
			}
		}		
	}
}
