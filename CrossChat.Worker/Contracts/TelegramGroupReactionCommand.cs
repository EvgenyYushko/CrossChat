namespace CrossChat.Worker.Contracts;

public class TelegramGroupReactionCommand
{
	public long ChatId { get; set; }
	public int MessageId { get; set; }
	public int GroupSettingsId { get; set; }
	public string? GroupTitle { get; set; }
}