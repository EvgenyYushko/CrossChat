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

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;

		// ID привязанного Instagram Business аккаунта (определяется автоматически)
		[MaxLength(100)]
		public string? LinkedInstagramBusinessId { get; set; }

		// === ЕЖЕДНЕВНЫЕ СТОРИС FACEBOOK ===
		public bool IsDailyStoriesEnabled { get; set; } = false;

		[MaxLength(5)]
		public string DailyStoryTime { get; set; } = "12:00";

		public DateTime? LastDailyStoryDate { get; set; }

		public string? UsedMediaIdsJson { get; set; } = "[]";

		public bool IsStoryOverlayTextEnabled { get; set; } = false;

		// Храним без ограничения длины (text) под большие списки фраз
		public string? StoryOverlayText { get; set; } = "Больше интересного по ссылке в описании!\nСсылка на Telegram в шапке профиля 👆";
	}
}
