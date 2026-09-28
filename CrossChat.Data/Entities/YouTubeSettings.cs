using System;
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
	}
}