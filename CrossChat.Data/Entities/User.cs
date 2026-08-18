using System.ComponentModel.DataAnnotations;
using static CrossChat.Data.Helpers.TimeZoneHelper;

namespace CrossChat.Data.Entities;

public class User
{
	public int Id { get; set; }

	[Required]
	public string GoogleId { get; set; } = string.Empty; // ID от гугла, чтобы узнавать юзера

	[Required]
	public string Email { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;
	public string? AvatarUrl { get; set; }

	public DateTime CreatedAt { get; set; } = DateTimeNow;

	public long? TelegramUserId { get; set; } // Хранит Telegram ID владельца аккаунта

	public ICollection<Profile> Profile { get; set; } = new List<Profile>();
}