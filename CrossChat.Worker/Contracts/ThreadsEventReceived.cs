namespace CrossChat.Worker.Contracts;
public record ThreadsEventReceived
{
	public string BotThreadsId { get; set; } = string.Empty; // Кому пришло
	public string Type { get; set; } = string.Empty;        // "replies", "mentions", "publish"
	public string MediaId { get; set; } = string.Empty;      // ID сообщения/поста
	public string? Text { get; set; }                       // Текст сообщения
	public string? Username { get; set; }                   // Кто написал
}
