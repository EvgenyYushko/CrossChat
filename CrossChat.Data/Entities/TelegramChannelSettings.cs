using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("TelegramChannelSettings")]
	public class TelegramChannelSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public long ChannelId { get; set; }         // ID канала в Telegram (например, -100123456789)
		public string ChannelTitle { get; set; } = string.Empty; // Название канала
		public string? ChannelUsername { get; set; } // @channel_name (если публичный)
		public string? ProfilePictureUrl { get; set; }

		public bool IsActive { get; set; } = true;
		public string SystemPrompt { get; set; } = "Ты ассистент публикаций в Telegram канале.";

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;

		// Автоматически одобрять заявки на вступление в канал
		public bool AutoApproveJoinRequests { get; set; } = false;

		// Уведомлять админа в Telegram о новом принятом подписчике
		public bool NotifyOnJoinRequests { get; set; } = true;

		// Уведомлять админа в Telegram, когда подписчик покинул канал
		public bool NotifyOnMemberLeft { get; set; } = false;

		// true = Группа / Супергруппа (например, чат комментариев канала); false = Канал
		public bool IsGroup { get; set; } = false;

		// Автоматически ставить реакцию на сообщения участников группы
		public bool AutoReactToMessages { get; set; } = true;

		// Эмодзи для реакций (можно одно "👍", или несколько подряд "👍❤️🔥" — выберет случайную)
		[MaxLength(100)]
		public string AllowedReactions { get; set; } = "👍";
	}
}
