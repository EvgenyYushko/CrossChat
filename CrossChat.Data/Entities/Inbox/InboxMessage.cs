using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities.Inbox
{
	[Table("InboxMessages")]
	public class InboxMessage
	{
		[Key]
		public int Id { get; set; }

		// Привязка к диалогу
		public int ConversationId { get; set; }
		[ForeignKey(nameof(ConversationId))]
		public virtual InboxConversation Conversation { get; set; } = null!;

		// Уникальный ID сообщения в соцсети (защита от дублей)
		[MaxLength(255)]
		public string? ExternalMessageId { get; set; }

		[Required]
		public string Text { get; set; } = string.Empty;

		// Направление: true = клиент написал нам, false = исходящее от нас
		public bool IsIncoming { get; set; } = true;

		// Авторство: true = ответил робот/ИИ, false = ответил человек (менеджер) руками
		public bool IsSentByBot { get; set; } = false;

		// Медиафайлы (если клиент прислал фото/голосовое)
		[MaxLength(500)]
		public string? MediaUrl { get; set; }

		[MaxLength(50)]
		public string? MediaType { get; set; } // IMAGE, AUDIO, VIDEO

		// Прочитано ли сообщение менеджером в веб-интерфейсе
		public bool IsReadByManager { get; set; } = false;

		public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
	}
}