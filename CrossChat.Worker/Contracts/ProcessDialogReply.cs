namespace CrossChat.Worker.Contracts;
public record ProcessDialogReply
{
	public string SenderId { get; set; } = string.Empty;
}
