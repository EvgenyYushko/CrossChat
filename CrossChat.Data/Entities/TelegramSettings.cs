using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CrossChat.Data.Entities;

namespace CrossChat.Data.Entities;
public class TelegramSettings
{
    [Key]
    [ForeignKey("User")]
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string? BotToken { get; set; }
    public string? BotUsername { get; set; }
    public bool IsActive { get; set; } = false;
    public string SystemPrompt { get; set; } = "Ты бот в Telegram. Отвечай вежливо.";
}