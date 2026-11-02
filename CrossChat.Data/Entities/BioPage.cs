using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("BioPages")]
	public class BioPage
	{
		[Key]
		public int Id { get; set; }

		// Привязка к профилю пользователя
		public int ProfileId { get; set; }
		[ForeignKey(nameof(ProfileId))]
		public virtual Profile Profile { get; set; } = null!;

		// Короткий URL (напр. "lanaroads" -> crosschat.ru/lanaroads)
		[Required]
		[MaxLength(60)]
		public string Slug { get; set; } = string.Empty;

		// Заголовок страницы (напр. "Lana Roads ✨")
		[Required]
		[MaxLength(100)]
		public string Title { get; set; } = string.Empty;

		// Краткое описание / статус под аватаркой
		[MaxLength(300)]
		public string? Bio { get; set; }

		// Аватарка (URL картинки или Base64)
		public string? AvatarUrl { get; set; }

		// Тип темы: "gradient-dark", "gradient-neon", "gradient-sunset", "custom-image"
		[MaxLength(50)]
		public string ThemeType { get; set; } = "gradient-dark";

		// CSS градиент или путь к загруженному фоновому фото
		[MaxLength(500)]
		public string? CustomBackgroundValue { get; set; }

		public bool IsActive { get; set; } = true;

		// Счетчик просмотров страницы
		public long ViewsCount { get; set; } = 0;

		public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
		public DateTime? UpdatedAt { get; set; }

		// Список кнопок-ссылок
		public virtual List<BioLinkItem> Links { get; set; } = new();
	}
}