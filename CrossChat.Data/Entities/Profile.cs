using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
	public class Profile
	{
		[DatabaseGenerated(DatabaseGeneratedOption.Identity)] 
		public int Id { get; set; }
		public int UserId { get; set; } // Владелец
		public User User { get; set; } = null!;

		public string Name { get; set; } = "Мой профиль";
		public string? AvatarUrl { get; set; }

		// Все соцсети теперь живут внутри одного Профиля
		public List<InstagramSettings> InstagramSettingsList { get; set; } = new();
		public List<FacebookSettings> FacebookSettingsList { get; set; } = new();
		public List<ThreadsSettings> ThreadsSettingsList { get; set; } = new();
		public List<XSettings> XSettingsList { get; set; } = new();
		public List<TelegramUserBotSettings> TelegramUserBotSettingsList { get; set; } = new();
		public TelegramSettings? TelegramSettings { get; set; }
		public List<TelegramChannelSettings> TelegramChannelSettingsList { get; set; } = new();
		public List<BlueSkySettings> BlueSkySettingsList { get; set; } = new();
	}
}
