using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CrossChat.Data.Entities
{
	[Table("TelegramChannelSettings")]
	public class TelegramChannelSettings
	{
		[Key]
		public int Id { get; set; }

		[ForeignKey("User")]
		public int UserId { get; set; }
		public User User { get; set; } = null!;

		public long ChannelId { get; set; }         // ID канала в Telegram (например, -100123456789)
		public string ChannelTitle { get; set; } = string.Empty; // Название канала
		public string? ChannelUsername { get; set; } // @channel_name (если публичный)

		public bool IsActive { get; set; } = true;
		public string SystemPrompt { get; set; } = "Ты ассистент публикаций в Telegram канале.";

		public int ProfileId { get; set; }
		public Profile Profile { get; set; } = null!;
	}
}
