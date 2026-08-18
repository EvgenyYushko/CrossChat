using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using static CrossChat.Data.Helpers.TimeZoneHelper;

namespace CrossChat.Data.Entities
{
	[Table("Reviews")]
	public class ReviewEntity
	{
		[Key]
		public int Id { get; set; }

		public int UserId { get; set; }

		[ForeignKey(nameof(UserId))]
		public virtual User User { get; set; } = null!;

		[Range(1, 5)]
		public int Rating { get; set; } // Оценка от 1 до 5 звезд

		[Required]
		public string Comment { get; set; } = string.Empty; // Текст отзыва

		public DateTime CreatedAt { get; set; } = DateTimeNow;
	}
}