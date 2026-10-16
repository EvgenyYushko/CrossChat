namespace CrossChat.Integrations.Models
{
	public class InstagramConversationSummaryDto
	{
		public string ConversationId { get; set; } = string.Empty;
		public string ClientId { get; set; } = string.Empty;
		public string? ClientUsername { get; set; }
		public string? LastMessageId { get; set; }
		public string? LastMessageText { get; set; }
		public DateTime LastMessageTime { get; set; }
		public bool IsLastMessageFromClient { get; set; }
	}
}