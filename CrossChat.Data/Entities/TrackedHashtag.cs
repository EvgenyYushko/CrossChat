using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("TrackedHashtags")]
	public class TrackedHashtag
	{
		[Key]
		public int Id { get; set; }

		public int UserId { get; set; }
		[ForeignKey(nameof(UserId))]
		public virtual User User { get; set; } = null!;

		// Хештег без решетки в нижнем регистре (например: "fitness")
		[Required]
		[MaxLength(100)]
		public string Tag { get; set; } = string.Empty;

		// Числовой ID хештега от Meta (кэшируем навсегда, чтобы не тратить лимит!)
		[MaxLength(100)]
		public string? InstagramHashtagId { get; set; }

		// Включен ли в авто-обновление роботом
		public bool IsAutoSync { get; set; } = true;

		// Когда последний раз опрашивали
		public DateTime? LastSyncedAt { get; set; }

		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

		public virtual List<ViralPost> Posts { get; set; } = new();
	}
}