namespace CrossChat.Worker.Contracts
{
	public class ProcessFacebookDialogReply
	{
		public string PageId { get; set; } = string.Empty;
		public string SenderId { get; set; } = string.Empty;
		public string ReplyId { get; set; } = string.Empty;
	}
}