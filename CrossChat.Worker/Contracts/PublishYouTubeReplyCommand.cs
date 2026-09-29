namespace CrossChat.Worker.Contracts;

public class PublishYouTubeReplyCommand
{
	public int BotDbId { get; set; }
	public string CommentId { get; set; } = string.Empty;
	public string ReplyText { get; set; } = string.Empty;
	public string Username { get; set; } = string.Empty;
}