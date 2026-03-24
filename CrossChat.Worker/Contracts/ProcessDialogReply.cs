namespace CrossChat.Worker.Contracts;
public record ProcessDialogReply
{
	public string SenderId { get; set; } = string.Empty;
	public string RecipientId { get; set; } = string.Empty;
	public string ReplyId { get; set; } = string.Empty;
}
