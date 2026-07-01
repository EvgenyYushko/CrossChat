using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities.Posting
{
	// Таблица состояний (вместо кучи таблиц FacebookTable, InstagramTable...)
	[Table("NetworkStates")]
	public class NetworkStateEntity
	{
		[Key]
		public int Id { get; set; }

		public Guid PostId { get; set; }

		[ForeignKey(nameof(PostId))]
		public virtual PostEntity Post { get; set; }

		// Какая это соцсеть? (Telegram, Instagram...)
		public int NetworkType { get; set; }

		// Текст поста для этой сети
		public string Caption { get; set; }

		// Статус (Pending, Error, Published)
		public int Status { get; set; }

		// ID конкретного подключенного аккаунта/бота (например, InstagramSettings.Id, TelegramUserBotSettings.Id и т.д.)
		public int? BotId { get; set; }
	}
}
