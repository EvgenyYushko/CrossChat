namespace CrossChat.Worker.Contracts;

public class YouTubeCommentReceived
{
	public int BotDbId { get; set; }
	public string ChannelId { get; set; } = string.Empty;
	public string VideoId { get; set; } = string.Empty;
	public string CommentId { get; set; } = string.Empty;
	public string Text { get; set; } = string.Empty;
	public string Username { get; set; } = string.Empty;
	public string? AuthorChannelId { get; set; }
}