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
	}
}
