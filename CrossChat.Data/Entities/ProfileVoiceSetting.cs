using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CrossChat.Data.Entities
{
    [Table("ProfileVoiceSettings")]
    public class ProfileVoiceSetting
    {
        [Key]
        public int Id { get; set; }

        // Связь 1-к-1 с профилем
        public int ProfileId { get; set; }
        [ForeignKey(nameof(ProfileId))]
        public virtual Profile Profile { get; set; } = null!;

        // Провайдер: "Gemini" или "ElevenLabs"
        [Required]
        [MaxLength(50)]
        public string Provider { get; set; } = "Gemini";

        // Имя голоса в Gemini (Puck, Charon, Kore, Fenrir, Aoede, Tavi) или Voice ID в ElevenLabs
        [Required]
        [MaxLength(100)]
        public string VoiceName { get; set; } = "Aoede";

        // Стиль речи (Whisper, Cheerful, Professional, Empathetic и т.д.)
        [MaxLength(50)]
        public string Style { get; set; } = "Whisper";

        // Темп речи (Natural, Rapid Fire, The Drift, Staccato)
        [MaxLength(50)]
        public string Pace { get; set; } = "Natural";

        // Акцент
        [MaxLength(50)]
        public string Accent { get; set; } = "American (Gen)";

        // Сцена / Роль для LLM (режиссерский промпт)
        [MaxLength(500)]
        public string Scene { get; set; } = "Нежная девушка, говорит мягко и доверительно";

        // Дополнительный контекст
        public string? SampleContext { get; set; }

        // Включена ли авто-озвучка
        public bool IsActive { get; set; } = true;

        // Поля на будущее для ElevenLabs (чтобы не переделывать БД потом!)
        [MaxLength(255)]
        public string? ElevenLabsVoiceId { get; set; }

        [MaxLength(255)]
        public string? ElevenLabsApiKey { get; set; }
    }
}