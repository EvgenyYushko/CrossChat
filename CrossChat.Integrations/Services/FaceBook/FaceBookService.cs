using System.Text;
using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;

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

		/// <summary>
		/// Получает публикации страницы Facebook с пагинацией и количеством лайков/комментов/репостов
		/// </summary>
		public async Task<FacebookFeedPageDto> GetPageFeedAsync(string pageId, string pageAccessToken, int limit = 12, string? after = null, string? before = null)
		{
			var result = new FacebookFeedPageDto();

			// Запрашиваем опубликованные посты страницы с авто-подсчетом реакций и комментариев
			string fields = "id,message,created_time,full_picture,permalink_url,shares,attachments{media_type,unshimmed_url},comments.summary(true),reactions.summary(true)";
			string url = $"https://graph.facebook.com/v24.0/{pageId}/published_posts?fields={fields}&limit={limit}&access_token={pageAccessToken}";

			if (!string.IsNullOrEmpty(after)) url += $"&after={after}";
			else if (!string.IsNullOrEmpty(before)) url += $"&before={before}";

			try
			{
				using var httpClient = new HttpClient();
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode)
				{
					var err = await response.Content.ReadAsStringAsync();
					Console.WriteLine($"[Facebook Feed] Ошибка загрузки постов страницы: {err}");
					return result;
				}

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);
				var root = doc.RootElement;

				if (root.TryGetProperty("data", out var dataArr))
				{
					foreach (var item in dataArr.EnumerateArray())
					{
						var post = new FacebookFeedPostDto
						{
							Id = item.GetProperty("id").GetString()!,
							Message = item.TryGetProperty("message", out var m) ? m.GetString() : null,
							PictureUrl = item.TryGetProperty("full_picture", out var fp) ? fp.GetString() : null,
							PermalinkUrl = item.TryGetProperty("permalink_url", out var pl) ? pl.GetString() : null
						};

						if (item.TryGetProperty("created_time", out var ct) && DateTime.TryParse(ct.GetString(), out var dt))
						{
							post.Timestamp = dt;
						}

						// Реакции
						if (item.TryGetProperty("reactions", out var reactObj) &&
							reactObj.TryGetProperty("summary", out var rSum) &&
							rSum.TryGetProperty("total_count", out var rCount))
						{
							post.LikesCount = rCount.GetInt32();
						}

						// Комментарии
						if (item.TryGetProperty("comments", out var commObj) &&
							commObj.TryGetProperty("summary", out var cSum) &&
							cSum.TryGetProperty("total_count", out var cCount))
						{
							post.CommentsCount = cCount.GetInt32();
						}

						// Репосты
						if (item.TryGetProperty("shares", out var sharesObj) &&
							sharesObj.TryGetProperty("count", out var sCount))
						{
							post.SharesCount = sCount.GetInt32();
						}

						// Определение типа медиа
						post.MediaType = !string.IsNullOrEmpty(post.PictureUrl) ? "PHOTO" : "STATUS";
						if (item.TryGetProperty("attachments", out var atts) &&
							atts.TryGetProperty("data", out var attData) && attData.GetArrayLength() > 0)
						{
							var firstAtt = attData[0];
							if (firstAtt.TryGetProperty("media_type", out var mt))
							{
								string typeStr = mt.GetString()?.ToUpper() ?? "";
								if (typeStr.Contains("VIDEO")) post.MediaType = "VIDEO";
								else if (typeStr.Contains("ALBUM")) post.MediaType = "ALBUM";
							}
						}

						result.Posts.Add(post);
					}
				}

				if (root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("cursors", out var cursors))
				{
					if (cursors.TryGetProperty("after", out var a)) result.AfterCursor = a.GetString();
					if (cursors.TryGetProperty("before", out var b)) result.BeforeCursor = b.GetString();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Feed] Исключение: {ex.Message}");
			}

			return result;
		}

		/// <summary>
		/// Получает глубокие инсайты публикации Facebook: уникальный охват, клики по посту и раскладку эмоций
		/// </summary>
		public async Task<FacebookPostInsightsDto> GetPostInsightsAsync(string postId, string pageAccessToken)
		{
			var insights = new FacebookPostInsightsDto();

			string metrics = "post_impressions_unique,post_impressions,post_engaged_users,post_clicks,post_reactions_by_type_total";
			string url = $"https://graph.facebook.com/v24.0/{postId}/insights?metric={metrics}&access_token={pageAccessToken}";

			try
			{
				using var httpClient = new HttpClient();
				var response = await httpClient.GetAsync(url);
				if (!response.IsSuccessStatusCode)
				{
					// Фоллбек для старых постов
					url = $"https://graph.facebook.com/v24.0/{postId}/insights?metric=post_impressions_unique,post_engaged_users&access_token={pageAccessToken}";
					response = await httpClient.GetAsync(url);
					if (!response.IsSuccessStatusCode) return insights;
				}

				var json = await response.Content.ReadAsStringAsync();
				using var doc = JsonDocument.Parse(json);

				if (doc.RootElement.TryGetProperty("data", out var dataArr))
				{
					foreach (var m in dataArr.EnumerateArray())
					{
						var name = m.GetProperty("name").GetString();
						if (!m.TryGetProperty("values", out var vals) || vals.GetArrayLength() == 0) continue;

						var valElem = vals[0].GetProperty("value");

						switch (name)
						{
							case "post_impressions_unique":
								if (valElem.ValueKind == JsonValueKind.Number) insights.Reach = valElem.GetInt32();
								break;
							case "post_impressions":
								if (valElem.ValueKind == JsonValueKind.Number) insights.Impressions = valElem.GetInt32();
								break;
							case "post_engaged_users":
								if (valElem.ValueKind == JsonValueKind.Number) insights.EngagedUsers = valElem.GetInt32();
								break;
							case "post_clicks":
								if (valElem.ValueKind == JsonValueKind.Number) insights.Clicks = valElem.GetInt32();
								break;
							case "post_reactions_by_type_total":
								if (valElem.ValueKind == JsonValueKind.Object)
								{
									insights.Reactions.Like = valElem.TryGetProperty("like", out var l) ? l.GetInt32() : 0;
									insights.Reactions.Love = valElem.TryGetProperty("love", out var lv) ? lv.GetInt32() : 0;
									insights.Reactions.Haha = valElem.TryGetProperty("haha", out var h) ? h.GetInt32() : 0;
									insights.Reactions.Wow = valElem.TryGetProperty("wow", out var w) ? w.GetInt32() : 0;
									insights.Reactions.Sad = valElem.TryGetProperty("sorry", out var s) ? s.GetInt32() : 0;
									insights.Reactions.Angry = valElem.TryGetProperty("anger", out var a) ? a.GetInt32() : 0;
								}
								break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Insights] Ошибка получения инсайтов поста {postId}: {ex.Message}");
			}

			// Расчет True ER: отношение вовлеченных пользователей к охвату
			if (insights.Reach > 0 && insights.EngagedUsers > 0)
			{
				insights.EngagementRate = Math.Round(((double)insights.EngagedUsers / insights.Reach) * 100.0, 1);

				if (insights.EngagementRate >= 12.0)
				{
					insights.EngagementBadge = "🚀 Вирусный хит";
					insights.BadgeColor = "#f43f5e";
				}
				else if (insights.EngagementRate >= 6.0)
				{
					insights.EngagementBadge = "🔥 Высокий отклик";
					insights.BadgeColor = "#ec4899";
				}
				else if (insights.EngagementRate >= 2.5)
				{
					insights.EngagementBadge = "⚡ Активная публикация";
					insights.BadgeColor = "#10b981";
				}
				else if (insights.EngagementRate >= 1.0)
				{
					insights.EngagementBadge = "👍 Хороший результат";
					insights.BadgeColor = "#38bdf8";
				}
				else
				{
					insights.EngagementBadge = "💤 Обычный пост";
					insights.BadgeColor = "#94a3b8";
				}
			}

			return insights;
		}

		/// <summary>
		/// Получает сводную аналитику страницы Facebook за последние 28 дней
		/// </summary>
		public async Task<FacebookPageInsightsDto> GetPageInsightsAsync(string pageId, string pageAccessToken)
		{
			var insights = new FacebookPageInsightsDto();

			try
			{
				using var httpClient = new HttpClient();

				// 1. Получаем количество подписчиков страницы
				var pageUrl = $"https://graph.facebook.com/v24.0/{pageId}?fields=followers_count,fan_count&access_token={pageAccessToken}";
				var pageResp = await httpClient.GetAsync(pageUrl);
				if (pageResp.IsSuccessStatusCode)
				{
					var pJson = await pageResp.Content.ReadAsStringAsync();
					using var pDoc = JsonDocument.Parse(pJson);
					if (pDoc.RootElement.TryGetProperty("followers_count", out var fc)) insights.FollowersCount = fc.GetInt32();
					else if (pDoc.RootElement.TryGetProperty("fan_count", out var fan)) insights.FollowersCount = fan.GetInt32();
				}

				// 2. Получаем ключевые метрики за 28 дней
				string metrics = "page_impressions_unique,page_engaged_users,page_post_engagements";
				string insightsUrl = $"https://graph.facebook.com/v24.0/{pageId}/insights?metric={metrics}&period=days_28&access_token={pageAccessToken}";

				var insResp = await httpClient.GetAsync(insightsUrl);
				if (insResp.IsSuccessStatusCode)
				{
					var insJson = await insResp.Content.ReadAsStringAsync();
					using var insDoc = JsonDocument.Parse(insJson);

					if (insDoc.RootElement.TryGetProperty("data", out var dataArr))
					{
						foreach (var m in dataArr.EnumerateArray())
						{
							var name = m.GetProperty("name").GetString();
							if (!m.TryGetProperty("values", out var vals) || vals.GetArrayLength() == 0) continue;

							// Берем последнее актуальное значение скользящего 28-дневного окна
							int lastIndex = vals.GetArrayLength() - 1;
							var lastVal = vals[lastIndex].GetProperty("value").GetInt32();

							switch (name)
							{
								case "page_impressions_unique":
									insights.Reach28Days = lastVal;
									break;
								case "page_engaged_users":
									insights.EngagedUsers28Days = lastVal;
									break;
								case "page_post_engagements":
									insights.PostEngagements28Days = lastVal;
									break;
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Facebook Page Insights] Исключение: {ex.Message}");
			}

			return insights;
		}

		public class FbMessageItem
		{
			public string Id { get; set; } = string.Empty;
			public string FromId { get; set; } = string.Empty;
			public string Text { get; set; } = string.Empty;
		}
	}
}
