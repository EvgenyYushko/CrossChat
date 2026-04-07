using System.Text.Json.Serialization;

namespace CrossChat.Integrations.Models
{
	public class ThreadsParent { public string Id { get; set; } }

	// Корневой ответ от API (массив данных + пагинация)
	public class ThreadsMediaResponse
	{
		[JsonPropertyName("data")]
		public List<ThreadsItem> Data { get; set; } = new();

		[JsonPropertyName("paging")]
		public ThreadsPaging? Paging { get; set; }
	}

	// Описание одного поста или комментария
	public class ThreadsItem
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("text")]
		public string? Text { get; set; }

		[JsonPropertyName("username")]
		public string? Username { get; set; }

		[JsonPropertyName("timestamp")]
		public DateTime Timestamp { get; set; }

		[JsonPropertyName("has_replies")]
		public bool HasReplies { get; set; }

		[JsonPropertyName("is_reply")]
		public bool IsReply { get; set; }

		// ВАЖНО: Определяет, наш ли это ответ
		[JsonPropertyName("is_reply_owned_by_me")]
		public bool IsReplyOwnedByMe { get; set; }

		// Ссылка на родительский пост
		[JsonPropertyName("root_post")]
		public ThreadsReference? RootPost { get; set; }

		// Ссылка на конкретное сообщение, на которое ответили
		[JsonPropertyName("replied_to")]
		public ThreadsReference? RepliedTo { get; set; }
	}

	// Простая ссылка на ID другого объекта
	public class ThreadsReference
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;
	}

	// Модели для пагинации (если захочешь листать историю дальше)
	public class ThreadsPaging
	{
		[JsonPropertyName("cursors")]
		public ThreadsCursors? Cursors { get; set; }

		[JsonPropertyName("next")]
		public string? Next { get; set; }
	}

	public class ThreadsCursors
	{
		[JsonPropertyName("before")]
		public string? Before { get; set; }

		[JsonPropertyName("after")]
		public string? After { get; set; }
	}
}
