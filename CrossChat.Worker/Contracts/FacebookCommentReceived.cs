namespace CrossChat.Worker.Contracts
{
	public class FacebookCommentReceived
	{
		public string PageId { get; set; } = string.Empty;
		public string CommentId { get; set; } = string.Empty;
		public string PostId { get; set; } = string.Empty;
		public string SenderId { get; set; } = string.Empty;
		public string SenderName { get; set; } = string.Empty;
		public string Text { get; set; } = string.Empty;
	}
}
