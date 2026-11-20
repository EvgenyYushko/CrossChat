using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities;

[Table("InstagramCustomerSentMedias")]
public class InstagramCustomerSentMedia
{
	[Key]
	public int Id { get; set; }

	public int CustomerId { get; set; }
	[ForeignKey(nameof(CustomerId))]
	public virtual InstagramBotCustomer Customer { get; set; } = null!;

	public int MediaItemId { get; set; }
	[ForeignKey(nameof(MediaItemId))]
	public virtual InstagramMediaItem MediaItem { get; set; } = null!;

	public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}