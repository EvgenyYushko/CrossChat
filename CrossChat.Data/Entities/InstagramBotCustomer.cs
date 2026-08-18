using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using static CrossChat.Data.Helpers.TimeZoneHelper;

namespace CrossChat.Data.Entities;

public class InstagramBotCustomer
{
	[Key]
	public int Id { get; set; }

	// Привязка к КОНКРЕТНОМУ боту (настройкам)
	[ForeignKey("InstagramSettings")]
	public int InstagramSettingsId { get; set; }
	public InstagramSettings InstagramSettings { get; set; } = null!;

	// ID пользователя в Инстаграме (senderId)
	public string InstagramSenderId { get; set; }

	// Данные для красивого отображения на сайте
	public string? Username { get; set; }
	public string? ProfilePictureUrl { get; set; }

	// --- БУДУЩИЕ НАСТРОЙКИ КОНКРЕТНОГО ЧЕЛОВЕКА ---
	public bool IsIgnored { get; set; } = false; // Галочка "Игнорировать"
	public string? CustomPrompt { get; set; }    // Персональный промпт

	public DateTime CreatedAt { get; set; } = DateTimeNow;

	// Навигационное свойство для логов
	public ICollection<BotResponseLog> ResponseLogs { get; set; } = new List<BotResponseLog>();
}
