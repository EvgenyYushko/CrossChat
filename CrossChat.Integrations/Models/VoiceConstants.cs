namespace CrossChat.Integrations.Models
{
    public static class VoiceConstants
    {
        // Доступные женские и мужские голоса в линейке Gemini Flash-TTS
        public static readonly List<string> GeminiVoices = new()
        {
            "Aoede",   // Женский, глубокий, выразительный (идеален для Whisper)
            "Tavi",    // Женский, мягкий, естественный
            "Kore",    // Женский, спокойный
            "Puck",    // Мужской/нейтральный, игривый
            "Charon",  // Мужской, солидный, глубокий
            "Fenrir"   // Мужской, уверенный
        };

        // Режимы темпа речи с подсказками для Director's Note
        public static readonly Dictionary<string, string> PaceMap = new()
        {
            { "Natural", "Natural conversational pace." },
            { "The Drift", "Slow, liquid, zero urgency. Long pauses for breath." },
            { "Rapid Fire", "Fast, energetic, no dead air. Sentences overlap slightly." },
            { "Staccato", "Short, clipped sentences with distinct pauses between words." }
        };

        // Эмоциональные стили
        public static readonly List<string> Styles = new()
        {
            "Whisper", "Cheerful", "Empathetic", "Professional", "Sad", "Angry", "Flirty"
        };

        // Акценты
        public static readonly List<string> Accents = new()
        {
            "Neutral", "American (Gen)", "American (South)", "British (RP)", "Australian"
        };
    }
}