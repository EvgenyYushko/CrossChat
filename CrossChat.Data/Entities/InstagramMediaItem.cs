using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities;

[Table("InstagramMediaItems")]
public class InstagramMediaItem
{
	[Key]
	public int Id { get; set; }

	public int InstagramSettingsId { get; set; }

	public int CategoryId { get; set; }
	[ForeignKey(nameof(CategoryId))]
	public virtual InstagramMediaCategory Category { get; set; } = null!;

	// "image" или "video"
	[Required]
	[MaxLength(20)]
	public string MediaType { get; set; } = "image";

	[Required]
	[MaxLength(255)]
	public string GoogleDriveFileId { get; set; } = string.Empty;

	[MaxLength(255)]
	public string FileName { get; set; } = string.Empty;

	[MaxLength(100)]
	public string MimeType { get; set; } = "image/jpeg";

	public long FileSizeBytes { get; set; }

	// Лёгкая превьюшка (~15 КБ Base64) для мгновенного отображения галереи без запросов к Google Drive
	[Column(TypeName = "text")]
	public string? ThumbnailBase64 { get; set; }

	public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

	public virtual ICollection<InstagramCustomerSentMedia> SentHistory { get; set; } = new List<InstagramCustomerSentMedia>();
}