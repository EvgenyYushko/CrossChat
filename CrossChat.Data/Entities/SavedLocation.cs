using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("SavedLocations")]
	public class SavedLocation
	{
		[Key]
		public int Id { get; set; }

		// Числовой ID точки в Meta (Facebook Page ID)
		[Required]
		[MaxLength(100)]
		public string LocationId { get; set; } = string.Empty;

		// Человеческое название места (напр. "Dubai, United Arab Emirates")
		[Required]
		[MaxLength(255)]
		public string Name { get; set; } = string.Empty;

		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	}
}