namespace CrossChat.Worker.Contracts;

public record TelegramProcessReply
{
	public long ChatId { get; set; }
	public string BotToken { get; set; } = string.Empty;
}
