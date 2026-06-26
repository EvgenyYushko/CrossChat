using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities.Posting
{
	// Главная таблица постов
	[Table("Posts")]
	public class PostEntity
	{
		[Key]
		public Guid Id { get; set; }

		// Внешний ключ к профилю
		public int ProfileId { get; set; }
		public virtual Profile Profile { get; set; } = null!;

		[Column(TypeName = "timestamp without time zone")]
		public DateTime CreatedAt { get; set; }

		[Column(TypeName = "timestamp without time zone")]
		public DateTime ShowDate { get; set; }

		// Храним Enum как int (0 = Public, 1 = Private)
		public int AccessLevel { get; set; }

		// Связь с картинками (Один пост -> Много картинок)
		public virtual List<PostImageEntity> Images { get; set; } = new();

		// Связь с состояниями сетей (Один пост -> Много состояний)
		public virtual List<NetworkStateEntity> NetworkStates { get; set; } = new();
	}
}
