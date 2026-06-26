using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities.Posting
{
	// Таблица картинок (Base64)
	[Table("PostImages")]
	public class PostImageEntity
	{
		[Key]
		public int Id { get; set; } // Внутренний ID картинки

		public Guid PostId { get; set; }

		[ForeignKey(nameof(PostId))]
		public virtual PostEntity Post { get; set; }

		// Ваша Base64 строка. 
		// В Postgres тип будет text (безлимитный), что ок для base64.
		public string Base64Data { get; set; }
	}
}
