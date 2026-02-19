using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities;

public class InstagramSettings
{
    [Key]
    [ForeignKey("User")]
    public int UserId { get; set; } // PK и FK одновременно (1 к 1)
    
    // Ссылка на юзера
    public User User { get; set; } = null!;

    // Данные от Facebook, когда он подключит аккаунт
    public string? InstagramBusinessId { get; set; } // ID бизнес-аккаунта
    public string? AccessToken { get; set; }         // Токен доступа
    public DateTime? TokenExpiresAt { get; set; }

    // Логика бота
    public string SystemPrompt { get; set; } = "Ты вежливый помощник. Отвечай кратко.";
    public bool IsActive { get; set; } = false; // Включен ли бот
}