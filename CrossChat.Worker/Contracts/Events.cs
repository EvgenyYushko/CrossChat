namespace CrossChat.Worker.Contracts;

// Событие: "Инстаграм прислал сообщение"
public record InstagramMessageReceived
{
	public string DialogId { get; set; } = string.Empty;
	public string MessageText { get; set; } = string.Empty;
	public DateTime ReceivedAt { get; set; }
}

