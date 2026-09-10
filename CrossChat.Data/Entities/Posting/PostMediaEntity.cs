using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CrossChat.Data.Emuns;

namespace CrossChat.Data.Entities.Posting
{
	[Table("PostMedia")]
	public class PostMediaEntity
	{
		[Key]
		public int Id { get; set; }

		public Guid PostId { get; set; }

		[ForeignKey(nameof(PostId))]
		public virtual PostEntity Post { get; set; } = null!;

		// Картинка или Видео
		public MediaType MediaType { get; set; }

		// ID файла на Google Диске
		[Required]
		[MaxLength(255)]
		public string GoogleDriveFileId { get; set; } = string.Empty;

		// Оригинальное имя (например: my_video.mp4)
		[MaxLength(255)]
		public string FileName { get; set; } = string.Empty;

		// MIME тип ("video/mp4", "image/jpeg")
		[MaxLength(100)]
		public string MimeType { get; set; } = string.Empty;

		// Размер в байтах
		public long FileSizeBytes { get; set; }

		// ID превью на Google Диске (для видео - 1-й кадр, для фото - миниатюра для календаря)
		[MaxLength(255)]
		public string? ThumbnailDriveFileId { get; set; }

		// Порядковый номер в карусели (0, 1, 2...)
		public int SortOrder { get; set; } = 0;
	}
}