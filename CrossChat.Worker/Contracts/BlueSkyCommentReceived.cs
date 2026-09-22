namespace CrossChat.Worker.Contracts
{
	public class BlueSkyCommentReceived
	{
		public int BotDbId { get; set; }
		public string CommentUri { get; set; } = string.Empty;
		public string CommentCid { get; set; } = string.Empty;
		public string RootUri { get; set; } = string.Empty;
		public string RootCid { get; set; } = string.Empty;
		public string AuthorDid { get; set; } = string.Empty;
		public string AuthorHandle { get; set; } = string.Empty;
		public string Text { get; set; } = string.Empty;
	}
}