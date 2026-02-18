using System.Text.Json.Serialization;

namespace CrossChat.Worker.Modules.Instagram.Models;

// 1. Корневой объект
public class InstagramWebhookPayload
{
	[JsonPropertyName("object")]
	public string Object { get; set; }

	[JsonPropertyName("entry")]
	public List<InstagramEntry> Entry { get; set; } = new();
}

// 2. Входная точка (Entry)
public class InstagramEntry
{
	[JsonPropertyName("id")]
	public string Id { get; set; } // ID аккаунта, куда пришло событие

	[JsonPropertyName("time")]
	public long Time { get; set; }

	// Основной массив для Директа (Сообщения, реакции, чтения)
	[JsonPropertyName("messaging")]
	public List<InstagramMessaging>? Messaging { get; set; }

	// Массив для изменений аккаунта (Комментарии, упоминания)
	[JsonPropertyName("changes")]
	public List<InstagramChange>? Changes { get; set; }
}

// 3. Элемент Messaging (Один из вариантов события)
public class InstagramMessaging
{
	[JsonPropertyName("sender")]
	public InstagramUser Sender { get; set; }

	[JsonPropertyName("recipient")]
	public InstagramUser Recipient { get; set; }

	[JsonPropertyName("timestamp")]
	public long Timestamp { get; set; }

	// --- Варианты содержимого (заполнено обычно только одно) ---

	[JsonPropertyName("message")]
	public InstagramMessage? Message { get; set; }

	[JsonPropertyName("read")]
	public InstagramRead? Read { get; set; }

	[JsonPropertyName("reaction")]
	public InstagramReaction? Reaction { get; set; }

	[JsonPropertyName("referral")]
	public InstagramReferral? Referral { get; set; }

	// В некоторых версиях API редактирование приходит отдельным полем
	[JsonPropertyName("message_edit")]
	public InstagramMessageEdit? MessageEdit { get; set; }
}

// 4. Детали обычного сообщения
public class InstagramMessage
{
	[JsonPropertyName("mid")]
	public string MessageId { get; set; }

	[JsonPropertyName("text")]
	public string? Text { get; set; }

	[JsonPropertyName("is_echo")]
	public bool IsEcho { get; set; } // true, если это сообщение от нас самих

	[JsonPropertyName("is_deleted")]
	public bool IsDeleted { get; set; }

	[JsonPropertyName("is_unsupported")]
	public bool IsUnsupported { get; set; }

	// Ответ на сторис
	[JsonPropertyName("reply_to")]
	public InstagramReplyTo? ReplyTo { get; set; }

	[JsonPropertyName("attachments")]
	public List<InstagramAttachment>? Attachments { get; set; }
}

// 5. Ответ на сторис (Reply To)
public class InstagramReplyTo
{
	[JsonPropertyName("story")]
	public InstagramStory? Story { get; set; }
}

public class InstagramStory
{
	[JsonPropertyName("url")]
	public string Url { get; set; }

	[JsonPropertyName("id")]
	public string Id { get; set; }
}

// 6. Реакции (Лайки, эмодзи)
public class InstagramReaction
{
	[JsonPropertyName("mid")]
	public string MessageId { get; set; } // ID сообщения, на которое отреагировали

	[JsonPropertyName("action")]
	public string Action { get; set; } // "react" или "unreact"

	[JsonPropertyName("reaction")]
	public string Reaction { get; set; } // Например, "love"

	[JsonPropertyName("emoji")]
	public string Emoji { get; set; } // Сам смайлик: ❤
}

// 7. Прочтение (Галочки)
public class InstagramRead
{
	[JsonPropertyName("mid")]
	public string MessageId { get; set; } // До какого сообщения прочитано
}

// 8. Реферальная ссылка (ig.me)
public class InstagramReferral
{
	[JsonPropertyName("ref")]
	public string Ref { get; set; }

	[JsonPropertyName("source")]
	public string Source { get; set; } // "SHORTLINK" и т.д.

	[JsonPropertyName("type")]
	public string Type { get; set; } // "OPEN_THREAD"
}

// 9. Редактирование сообщения
public class InstagramMessageEdit
{
	[JsonPropertyName("mid")]
	public string MessageId { get; set; }

	[JsonPropertyName("text")]
	public string Text { get; set; } // Новый текст

	[JsonPropertyName("num_edit")]
	public int NumEdit { get; set; } // Сколько раз редактировали
}

// 10. Вложения (Картинки, видео)
public class InstagramAttachment
{
	[JsonPropertyName("type")]
	public string Type { get; set; } // "image", "video", "audio"

	[JsonPropertyName("payload")]
	public InstagramAttachmentPayload Payload { get; set; }
}

public class InstagramAttachmentPayload
{
	[JsonPropertyName("url")]
	public string Url { get; set; }
}

// 11. Пользователь
public class InstagramUser
{
	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("username")]
	public string? Username { get; set; }
}

// 12. Changes (Комментарии к постам)
public class InstagramChange
{
	[JsonPropertyName("field")]
	public string Field { get; set; } // "comments"

	[JsonPropertyName("value")]
	public InstagramChangeValue Value { get; set; }
}

public class InstagramChangeValue
{
	[JsonPropertyName("id")]
	public string Id { get; set; } // ID комментария

	[JsonPropertyName("text")]
	public string Text { get; set; }

	[JsonPropertyName("from")]
	public InstagramUser From { get; set; }

	[JsonPropertyName("media")]
	public InstagramMediaShort Media { get; set; }
}

public class InstagramMediaShort
{
	[JsonPropertyName("id")]
	public string Id { get; set; }

	[JsonPropertyName("media_product_type")]
	public string ProductType { get; set; }
}