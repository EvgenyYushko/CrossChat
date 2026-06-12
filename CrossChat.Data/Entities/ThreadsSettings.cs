using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities;
public class ThreadsSettings
{
	[Key]
	public int Id { get; set; }

	[ForeignKey("User")]
	public int UserId { get; set; }
	public User User { get; set; } = null!;

	public string? AccessToken { get; set; }
	public string? ThreadsUserId { get; set; } // ID пользователя в Threads
	public string? Username { get; set; }
	public string? ProfilePictureUrl { get; set; }
	public DateTime? TokenExpiresAt { get; set; }

	public bool IsActive { get; set; } = false;
	public string SystemPrompt { get; set; } = "Ты ассистент в Threads. Отвечай кратко.";

	public DateTime? LastProcessedAt { get; set; }

	public int ProfileId { get; set; }
	public Profile Profile { get; set; } = null!;
}
