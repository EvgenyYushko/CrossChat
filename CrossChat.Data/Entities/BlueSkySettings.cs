using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	public class BlueSkySettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public string? Did { get; set; }        // Уникальный ID (например, did:plc:z7...)
		public string? Handle { get; set; }     // Никнейм (например, user.bsky.social)
		public string? AccessToken { get; set; }
		public DateTime? TokenExpiresAt { get; set; }
		public string? RefreshToken { get; set; }
		public string? PdsUrl { get; set; }     // URL сервера пользователя
		public string? PrivateKeyJson { get; set; }
		public string? ProfilePictureUrl { get; set; }

		public bool IsActive { get; set; } = false;
		public string SystemPrompt { get; set; } = "Ты ассистент в BlueSky. Отвечай лаконично.";

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;

		// Включение ответов в ЛС (чат)
		public bool IsDirectEnabled { get; set; } = true;

		// Включение ответов на комментарии (реплаи и упоминания)
		public bool IsCommentsEnabled { get; set; } = true;

		// Режим ответов на комментарии: 1 = ИИ, 2 = Шаблоны (дефолт), 3 = Комбинированный
		public int CommentReplyMode { get; set; } = 2;

		// Шаблоны ответов со Spintax
		public string? CommentTemplates { get; set; } = "{Спасибо|Благодарю|Пасиб} за {отклик|комментарий}! ❤️\nРады видеть вас здесь! ✨\n{Заглядывайте|Заходите} почаще 😊";

		// Промпт для комментариев ИИ
		public string CommentPrompt { get; set; } = "Ты ассистент в BlueSky. Отвечай на комментарии дружелюбно, живо и кратко.";

		// Дата и время последнего обработанного комментария строго в UTC (защита от повторов)
		public DateTime? LastProcessedAt { get; set; }

		// === АВТО-КОММЕНТИНГ ПО ХЕШТЕГАМ / ТЕМАМ (ОБХОД И АКТИВНОСТЬ) ===
		// Включен ли автопоиск и комментирование чужих постов
		public bool IsHashtagEngagementEnabled { get; set; } = false;

		// Ключевые хештеги/слова через запятую (напр. "#fitness, #smm, #marketing")
		public string? EngagementHashtags { get; set; } = "#fitness, #lifestyle";

		// Шаблоны комментариев со Spintax (напр. "{Круто|Отличный пост|Интересно}! 🔥")
		public string? EngagementCommentTemplates { get; set; } = "{Отличный пост|Крутые мысли|Полностью согласна|Интересный взгляд}! ✨\n{Спасибо, что делитесь|Полезно было почитать} 👍";

		// JSON-массив URI постов, которые мы уже комментировали (защита от повторов)
		public string? UsedPostUrisJson { get; set; } = "[]";

		// Когда последний раз запускался обход
		public DateTime? LastHashtagEngagementAt { get; set; }
	}
}
