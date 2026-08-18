using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Models.Posting;
using static CrossChat.Integrations.Helpers.TimeZoneHelper;

namespace CrossChat.Integrations.Models
{
	public class BlogPost
	{
		public BlogPost() { }

		public Guid Id { get; set; } = Guid.NewGuid();
		public int ProfileId { get; set; }

		public List<string> Images { get; set; } = new();
		public DateTime CreatedAt { get; set; } = DateTimeNow;
		public DateTime ShowDate { get; set; }

		public AccessLevel Access { get; set; } = AccessLevel.Public; // Пост публичный или приватный?

		// Ключ словаря теперь - строка вида "{NetworkType}_{BotId}" (например, "Instagram_5")
		public Dictionary<string, NetworkPostData> Networks { get; set; } = new();
	}
}
