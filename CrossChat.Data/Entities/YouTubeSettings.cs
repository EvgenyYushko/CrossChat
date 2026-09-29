using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("YouTubeSettings")]
	public class YouTubeSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;

		// Данные YouTube канала
		[MaxLength(100)]
		public string ChannelId { get; set; } = string.Empty; // Например: UC_x5XG1OV2P6uZZ5FSM9Ttw

		public string ChannelTitle { get; set; } = string.Empty; // Название канала

		public string? CustomUrl { get; set; } // @handle канала (например, @CrossChat)

		public string? ProfilePictureUrl { get; set; } // Аватарка канала

		// Токены авторизации
		public string? AccessToken { get; set; }
		public string? RefreshToken { get; set; }
		public DateTime? TokenExpiresAt { get; set; }

		// Базовая статистика (для красивой карточки)
		public ulong SubscriberCount { get; set; } = 0;
		public ulong VideoCount { get; set; } = 0;

		public bool IsActive { get; set; } = true;

		public string SystemPrompt { get; set; } = "Ты ассистент YouTube канала. Пиши цепляющие описания и теги для видео.";

		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

		// === НАСТРОЙКИ АВТООТВЕТОВ НА КОММЕНТАРИИ ===

		// Включен ли автоответчик
		public bool IsCommentsEnabled { get; set; } = false;

		// Режим ответов: 1 = Только ИИ, 2 = Только шаблоны (Spintax), 3 = Комбинированный
		public int CommentReplyMode { get; set; } = 2;

		// Шаблоны со Spintax
		public string? CommentTemplates { get; set; } = "{Спасибо|Благодарю|Пасиб} за {отклик|комментарий}! ❤️\nРады видеть вас на нашем канале! ✨\n{Заглядывайте|Заходите} почаще 😊";

		// Промпт для ИИ (Gemini)
		public string CommentPrompt { get; set; } = "Ты автор YouTube канала. Отвечай на комментарии дружелюбно, живо и кратко на том же языке, на котором написан комментарий.";

		// Дата последнего опроса, чтобы не запрашивать древние комментарии
		public DateTime? LastCommentProcessedAt { get; set; }
	}
}