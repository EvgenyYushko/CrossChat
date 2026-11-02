using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	[Table("BioPageEvents")]
	public class BioPageEvent
	{
		[Key]
		public long Id { get; set; }

		// Привязка к странице
		public int BioPageId { get; set; }
		[ForeignKey(nameof(BioPageId))]
		public virtual BioPage BioPage { get; set; } = null!;

		// Привязка к кнопке (null = просмотр страницы, число = клик по конкретной кнопке)
		public int? BioLinkItemId { get; set; }
		[ForeignKey(nameof(BioLinkItemId))]
		public virtual BioLinkItem? BioLinkItem { get; set; }

		// Тип события: "view" (просмотр страницы) или "click" (клик по кнопке)
		[Required]
		[MaxLength(20)]
		public string EventType { get; set; } = "view";

		// Реальный IP посетителя
		[MaxLength(45)]
		public string? IpAddress { get; set; }

		// ПЛАН А: Страна из заголовка (US, RU, BR, DE) или null для Плана Б
		[MaxLength(10)]
		public string? CountryCode { get; set; }

		// Источник: instagram, facebook, bluesky, telegram, x, threads, youtube, direct
		[Required]
		[MaxLength(50)]
		public string TrafficSource { get; set; } = "direct";

		// Полный Referer (откуда кликнули)
		[MaxLength(500)]
		public string? RefererUrl { get; set; }

		// Устройство: mobile, desktop, tablet
		[MaxLength(20)]
		public string DeviceType { get; set; } = "mobile";

		// ОС: ios, android, windows, macos, linux
		[MaxLength(30)]
		public string OperatingSystem { get; set; } = "unknown";

		// Браузер (включая Instagram In-App / Facebook In-App):
		[MaxLength(50)]
		public string Browser { get; set; } = "unknown";

		// Язык устройства (напр. "pt", "ru", "en")
		[MaxLength(20)]
		public string? Language { get; set; }

		public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
	}
}