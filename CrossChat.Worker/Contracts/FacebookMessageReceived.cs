namespace CrossChat.Worker.Contracts
{
	public class FacebookMessageReceived
	{
		public int BotDbId { get; set; }
		public string PageId { get; set; } = string.Empty;
		public string SenderId { get; set; } = string.Empty;
		public string MessageId { get; set; } = string.Empty;
		public string Text { get; set; } = string.Empty;
		public int AttachmentCount { get; set; } = 0;

		// === НОВЫЕ ПОЛЯ ДЛЯ МЕДИАФАЙЛОВ ===
		public string? MediaUrl { get; set; }
		public string? MediaType { get; set; } // "image", "video", "audio"
	}
}