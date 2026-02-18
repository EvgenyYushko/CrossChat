namespace CrossChat.Worker.Contracts;

// Событие: "Инстаграм прислал сообщение"
public record InstagramMessageReceived
{
	public string SenderId { get; set; } = string.Empty;
	public string RecipientId { get; set; } = string.Empty;
	public string MessageId { get; set; } = string.Empty;
	public DateTime ReceivedAt { get; set; }
}

