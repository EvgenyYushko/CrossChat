using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	public class TelegramUserBotSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		// Данные для "Injection"
		public int DcId { get; set; }
		public string AuthKey { get; set; } = string.Empty;
		public long TgUserId { get; set; }
		public string TgUserName { get; set; }

		// Прокси
		public string? ProxyHost { get; set; }
		public int? ProxyPort { get; set; }
		public string? ProxyUser { get; set; }
		public string? ProxyPass { get; set; }

		// САМОЕ ВАЖНОЕ: Содержимое файла сессии
		public byte[]? SessionData { get; set; }

		public bool IsActive { get; set; } = false;
		public string SystemPrompt { get; set; } = "Ты ассистент. Отвечай вежливо.";

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;
	}
}
