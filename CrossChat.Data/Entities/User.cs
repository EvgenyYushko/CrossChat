using System.ComponentModel.DataAnnotations;

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

	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	// Связь 1 к 1 с настройками Инстаграма
	public InstagramSettings? InstagramSettings { get; set; }
}