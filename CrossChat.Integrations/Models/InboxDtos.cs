namespace CrossChat.Integrations.Models
{
	public class InboxConversationDto
	{
		public int Id { get; set; }
		public int ProfileId { get; set; }
		public int NetworkType { get; set; }
		public int BotId { get; set; }
		public string ExternalUserId { get; set; } = string.Empty;

		 // Добавляем ID чата/ветки (convoId для BlueSky, pageId для FB):
        public string? ExternalChatId { get; set; } 

		public string? Username { get; set; }
		public string? FullName { get; set; }
		public string? AvatarUrl { get; set; }

		// Данные для Human Takeover
		public bool IsHumanTakeover { get; set; }
		public DateTime? HumanTakeoverUntil { get; set; }
	}
}