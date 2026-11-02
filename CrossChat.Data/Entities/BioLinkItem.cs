using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("BioLinkItems")]
	public class BioLinkItem
	{
		[Key]
		public int Id { get; set; }

		public int BioPageId { get; set; }
		[ForeignKey(nameof(BioPageId))]
		public virtual BioPage BioPage { get; set; } = null!;

		// Название кнопки (напр. "✈️ Мой закрытый Telegram")
		[Required]
		[MaxLength(100)]
		public string Title { get; set; } = string.Empty;

		// Целевой URL (напр. "https://t.me/my_channel")
		[Required]
		[MaxLength(500)]
		public string Url { get; set; } = string.Empty;

		// Иконка: "telegram", "instagram", "youtube", "bluesky", "threads", "x", "custom"
		[MaxLength(50)]
		public string IconType { get; set; } = "custom";

		// Порядковый номер сортировки (чтобы можно было менять порядок кнопок)
		public int SortOrder { get; set; } = 0;

		// Выделять ли кнопку пульсацией (для привлечения 80% кликов)
		public bool IsHighlighted { get; set; } = false;

		// Счётчик кликов по этой конкретной кнопке
		public long ClicksCount { get; set; } = 0;

		public bool IsActive { get; set; } = true;
	}
}