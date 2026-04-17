using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	public class FacebookSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]

		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public string? PageId { get; set; }           // ID самой страницы Facebook
		public string? PageAccessToken { get; set; }    // Токен именно СТРАНИЦЫ (не юзера)
		public string? PageName { get; set; }
		public string? ProfilePictureUrl { get; set; }

		public bool IsActive { get; set; } = false;
		public string SystemPrompt { get; set; } = "Ты ассистент на странице Facebook. Отвечай вежливо.";

		public DateTime? TokenExpiresAt { get; set; } // Для страниц они часто бессрочные, но лучше хранить
	}
}
