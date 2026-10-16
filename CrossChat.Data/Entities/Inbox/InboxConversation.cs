using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities.Inbox
{
	[Table("InboxConversations")]
	public class InboxConversation
	{
		[Key]
		public int Id { get; set; }

		// 1. ПРИВЯЗКА К ПРОФИЛЮ (Гарантирует изоляцию проектов)
		public int ProfileId { get; set; }
		[ForeignKey(nameof(ProfileId))]
		public virtual Profile Profile { get; set; } = null!;

		// 2. ИДЕНТИФИКАЦИЯ СОЦСЕТИ (Храним Enum как int, в точности как в NetworkStateEntity)
		public int NetworkType { get; set; }

		// ID конкретной настройки бота (InstagramSettings.Id, FacebookSettings.Id и т.д.)
		public int BotId { get; set; }

		// Внешний ID диалога в API (convoId в BlueSky, threadId, chatId)
		[MaxLength(255)]
		public string? ExternalChatId { get; set; }

		// 3. ДАННЫЕ КЛИЕНТА
		[Required]
		[MaxLength(255)]
		public string ExternalUserId { get; set; } = string.Empty; // PSID, Telegram UserId, BlueSky DID

		[MaxLength(255)]
		public string? Username { get; set; } // @username

		[MaxLength(255)]
		public string? FullName { get; set; } // Имя Фамилия

		public string? AvatarUrl { get; set; }

		// 4. ДАННЫЕ ДЛЯ БЫСТРОГО РЕНДЕРА СПИСКА
		[MaxLength(500)]
		public string? LastMessageSnippet { get; set; }

		public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

		public int UnreadCount { get; set; } = 0;

		// 5. РЕЖИМ HUMAN TAKEOVER (ПЕРЕХВАТ У ИИ)
		public bool IsHumanTakeover { get; set; } = false;

		// До какого момента времени ИИ заглушен (например, на 2 часа после ответа менеджера)
		public DateTime? HumanTakeoverUntil { get; set; }

		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

		// Навигационное свойство сообщений
		public virtual ICollection<InboxMessage> Messages { get; set; } = new List<InboxMessage>();
	}
}