using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities;

[Table("InstagramMediaCategories")]
public class InstagramMediaCategory
{
	[Key]
	public int Id { get; set; }

	public int InstagramSettingsId { get; set; }
	[ForeignKey(nameof(InstagramSettingsId))]
	public virtual InstagramSettings InstagramSettings { get; set; } = null!;

	[Required]
	[MaxLength(100)]
	public string Name { get; set; } = string.Empty;

	// Необязательное описание-подсказка для модели (напр. "фото в полный рост, ноги, туфли")
	[MaxLength(300)]
	public string? Description { get; set; }

	// Дефолтная категория ("Общее"), в которую бот падает при общих просьбах или если целевая закончилась
	public bool IsDefault { get; set; } = false;

	public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

	public virtual ICollection<InstagramMediaItem> Items { get; set; } = new List<InstagramMediaItem>();
}