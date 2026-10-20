namespace CrossChat.Worker.Contracts;

// Событие: "Инстаграм прислал сообщение"
public record InstagramMessageReceived
{
	public string SenderId { get; set; } = string.Empty;
	public string RecipientId { get; set; } = string.Empty;
	public string MessageId { get; set; } = string.Empty;
	public string? Text { get; set; }
	public DateTime ReceivedAt { get; set; }

	public int AttachmentCount  { get; set; }

	// НОВЫЕ ПОЛЯ ДЛЯ МЕДИАФАЙЛОВ:
	public string? MediaUrl { get; set; }
	public string? MediaType { get; set; } // "image", "video", "audio"
}

