using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	public class XSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public string? AccessToken { get; set; }
		public string? RefreshToken { get; set; }
		public DateTime? TokenExpiresAt { get; set; }

		public string? XUserId { get; set; }      // ID пользователя в X
		public string? ScreenName { get; set; }   // @username
		public string? ProfilePictureUrl { get; set; }

		public bool IsActive { get; set; } = false;
		public string SystemPrompt { get; set; } = "Ты — креативный автор в X (Twitter). Пиши цепляющие посты.";
	}
}
