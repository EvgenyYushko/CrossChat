using System.Text;
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
			var unreadConversation = new List<FbConversation>();

			// Экранируем вложенные поля, чтобы Meta не падала в 500 Internal Server Error
			string fields = "id,unread_count,messages.limit(1){from,message}";

			// Используем v24.0 единым стандартом
			string url = $"https://graph.facebook.com/v24.0/{pageId}/conversations" +
						 $"?platform=messenger" +
						 $"&fields={Uri.EscapeDataString(fields)}" +
						 $"&access_token={token}";

			try
			{
				using (var httpClient = new HttpClient())
				{
					var response = await httpClient.GetAsync(url);
					if (!response.IsSuccessStatusCode)
					{
						string error = await response.Content.ReadAsStringAsync();
						Console.WriteLine($"Ошибка получения диалогов FB (HTTP {response.StatusCode}): {error}");
						return unreadConversation;
					}

					var json = await response.Content.ReadAsStringAsync();
					var conversationData = JsonSerializer.Deserialize<FbConversationResponse>(json);

					if (conversationData?.data == null) return unreadConversation;

					foreach (var convo in conversationData.data)
					{
						if (convo.messages?.data == null || !convo.messages.data.Any()) continue;

						var lastMsg = convo.messages.data.First();

						// Отправитель не должен быть самой страницей
						if (lastMsg.from?.id != pageId)
						{
							unreadConversation.Add(convo);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Исключение при получении диалогов FB: {ex.Message}");
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

		/// <summary>
		/// Публикует первый комментарий к посту или Reels на странице Facebook
		/// </summary>
		public async Task<string?> CreateCommentAsync(string postId, string text, string pageAccessToken)
		{
			if (string.IsNullOrWhiteSpace(postId) || string.IsNullOrWhiteSpace(text))
				return null;

			string url = $"https://graph.facebook.com/v24.0/{postId}/comments";

			var postData = new Dictionary<string, string>
			{
				{ "message", text },
				{ "access_token", pageAccessToken }
			};

			try
			{
				using var httpClient = new HttpClient();
				using var content = new FormUrlEncodedContent(postData);

				var response = await httpClient.PostAsync(url, content);
				var responseContent = await response.Content.ReadAsStringAsync();

				if (response.IsSuccessStatusCode)
				{
					using var doc = JsonDocument.Parse(responseContent);
					var commentId = doc.RootElement.TryGetProperty("id", out var idElem) ? idElem.GetString() : null;
					Console.WriteLine($"[Facebook] ✅ Первый комментарий успешно опубликован к посту {postId}. ID комментария: {commentId}");
					return commentId;
				}
				else
				{
					Console.WriteLine($"[Facebook] ❌ Ошибка публикации первого комментария к {postId}: {responseContent}");
					return null;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook] Исключение при отправке первого комментария: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Отправляет ответ на конкретный комментарий пользователя на странице Facebook
		/// </summary>
		public async Task<bool> ReplyToCommentAsync(string commentId, string text, string pageAccessToken)
		{
			if (string.IsNullOrWhiteSpace(commentId) || string.IsNullOrWhiteSpace(text))
				return false;

			string url = $"https://graph.facebook.com/v24.0/{commentId}/comments";

			var postData = new Dictionary<string, string>
			{
				{ "message", text },
				{ "access_token", pageAccessToken }
			};

			try
			{
				using var httpClient = new HttpClient();
				using var content = new FormUrlEncodedContent(postData);

				var response = await httpClient.PostAsync(url, content);
				if (response.IsSuccessStatusCode)
				{
					Console.WriteLine($"[Facebook] ✅ Ответ на комментарий {commentId} успешно отправлен!");
					return true;
				}

				var errorResult = await response.Content.ReadAsStringAsync();
				Console.WriteLine($"[Facebook] ❌ Ошибка ответа на комментарий (HTTP {response.StatusCode}): {errorResult}");
				return false;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook] Исключение при ответе на комментарий: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Получает историю переписки с конкретным пользователем (PSID) на странице Facebook
		/// </summary>
		public async Task<List<FbMessageItem>> GetMessagesBySenderIdAsync(string pageId, string senderId, string token, int limit = 10)
		{
			var result = new List<FbMessageItem>();

			// В Graph API диалог с конкретным пользователем запрашивается через user_id
			string url = $"https://graph.facebook.com/v24.0/{pageId}/conversations?user_id={senderId}&fields=messages.limit({limit}){{from,message,created_time}}&access_token={token}";

			try
			{
				using var httpClient = new HttpClient();
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode) return result;

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);

				if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.GetArrayLength() > 0)
				{
					var convo = dataArr[0];
					if (convo.TryGetProperty("messages", out var msgs) && msgs.TryGetProperty("data", out var mArr))
					{
						foreach (var m in mArr.EnumerateArray())
						{
							string fromId = m.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fid) ? fid.GetString() ?? "" : "";
							string text = m.TryGetProperty("message", out var msgElem) ? msgElem.GetString() ?? "" : "";
							string id = m.TryGetProperty("id", out var mid) ? mid.GetString() ?? "" : "";

							result.Add(new FbMessageItem
							{
								Id = id,
								FromId = fromId,
								Text = text
							});
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Messages] Ошибка получения истории: {ex.Message}");
			}

			return result;
		}

		/// <summary>
		/// Отправляет статус "печатает..." (typing_on) в чат Messenger
		/// </summary>
		public async Task SetTypingStatusAsync(string recipientId, string token)
		{
			string url = $"https://graph.facebook.com/v24.0/me/messages";
			var payload = new
			{
				recipient = new { id = recipientId },
				sender_action = "typing_on",
				access_token = token
			};

			try
			{
				using var httpClient = new HttpClient();
				var json = JsonSerializer.Serialize(payload);
				var content = new StringContent(json, Encoding.UTF8, "application/json");
				await httpClient.PostAsync(url, content);
			}
			catch { }
		}

		public class FbMessageItem
		{
			public string Id { get; set; } = string.Empty;
			public string FromId { get; set; } = string.Empty;
			public string Text { get; set; } = string.Empty;
		}
	}
}
