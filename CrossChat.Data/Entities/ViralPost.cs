using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("ViralPosts")]
	public class ViralPost
	{
		[Key]
		public int Id { get; set; }

		public int TrackedHashtagId { get; set; }
		[ForeignKey(nameof(TrackedHashtagId))]
		public virtual TrackedHashtag TrackedHashtag { get; set; } = null!;

		// Уникальный ID поста в Instagram (для защиты от дублей)
		[Required]
		[MaxLength(100)]
		public string InstagramMediaId { get; set; } = string.Empty;

		public string? Caption { get; set; }

		[MaxLength(50)]
		public string MediaType { get; set; } = "IMAGE"; // IMAGE, VIDEO, CAROUSEL_ALBUM

		public string? MediaUrl { get; set; }

		[MaxLength(500)]
		public string? Permalink { get; set; } // Прямая ссылка на пост в Instagram

		public int LikeCount { get; set; }
		public int CommentsCount { get; set; }

		// Все извлеченные из описания хештеги (через запятую или пробел)
		public string? ExtractedHashtags { get; set; }

		public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
	}
}