using System.Text.Json.Serialization;

namespace CrossChat.Integrations.Models;

// Ответ на запрос списка диалогов
public class ConversationsResponse
{
    [JsonPropertyName("data")]
    public List<ConversationItem> Data { get; set; } = new();
}

public class ConversationItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("updated_time")]
    public string UpdatedTime { get; set; }
}

// Ответ на запрос сообщений в диалоге
public class ConversationMessagesResponse
{
    [JsonPropertyName("messages")]
    public MessageDataWrapper Messages { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }
}

public class MessageDataWrapper
{
    [JsonPropertyName("data")]
    public List<MessageItem> Data { get; set; } = new();
}

public class MessageItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("created_time")]
    public string CreatedTime { get; set; }

    [JsonPropertyName("from")]
    public InstagramUserShort From { get; set; }

    [JsonPropertyName("message")]
    public string Text { get; set; }

    // Пока упростим attachments и to, чтобы не раздувать код, 
    // если они нужны для логики - раскомментируй
    // [JsonPropertyName("attachments")]
    // public AttachmentDataWrapper Attachments { get; set; }
}

public class InstagramUserShort
{
    [JsonPropertyName("username")]
    public string Username { get; set; }
    
    [JsonPropertyName("id")]
    public string Id { get; set; }
}