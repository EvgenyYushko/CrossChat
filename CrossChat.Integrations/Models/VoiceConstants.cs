namespace CrossChat.Integrations.Models
{
    public static class VoiceConstants
    {
        // 🎙️ ВСЕ АКТУАЛЬНЫЕ ГОЛОСА ИЗ GOOGLE AI STUDIO (С ПЕРЕВОДОМ ТЕМБРА)
        public static readonly List<VoiceOption> Voices = new()
        {
            // ТОПОВЫЕ ДЛЯ ЖЕНСКИХ ПЕРСОНАЖЕЙ (ИНФЛЮЕНСЕРЫ / БЛОГЕРЫ)
            new("Tavi", "Tavi — Женский (Мягкий, тёплый, естественный) ⭐"),
            new("Aoede", "Aoede — Женский (Воздушный, интимный, нежный) ⭐"),
            new("Hali", "Hali — Женский (Успокаивающий, уютный)"),
            new("Kira", "Kira — Женский (Уверенный, дерзкий, прохладный)"),
            new("Cleo", "Cleo — Женский (Вовлекающий, выразительный)"),
            new("Rina", "Rina — Женский (Расслабленный, мягкий)"),
            new("Sola", "Sola — Женский (Игривый, высокий тон)"),
            new("Zali", "Zali — Женский (Эмпатичный, душевный)"),
            new("Tova", "Tova — Женский (Бархатный, убаюкивающий)"),
            new("Lumi", "Lumi — Женский (Тёплый, бархатный низкий тон)"),
            new("Nika", "Nika — Женский (Привлекательный, дружелюбный)"),
            new("Enya", "Enya — Женский (Деловой, прямой, уверенный)"),
            new("Fola", "Fola — Женский (Чёткий, открытый)"),
            new("Lora", "Lora — Женский (Ясный, звонкий)"),
            new("Nyla", "Nyla — Женский (Профессиональный, чистый)"),
            new("Tari", "Tari — Женский (Уверенный, чёткий)"),
            new("Veda", "Veda — Женский (Открытый, высокий тон)"),
            new("Zuri", "Zuri — Женский (Естественный, чистый)"),
            new("Kore", "Kore — Женский (Строгий, решительный)"),
            new("Leda", "Leda — Женский (Юный, лёгкий)"),
            new("Despina", "Despina — Женский (Плавный, обволакивающий)"),
            new("Vindemiatrix", "Vindemiatrix — Женский (Мягкий, трепетный)"),

            // МУЖСКИЕ ГОЛОСА
            new("Puck", "Puck — Мужской (Игривый, живой, открытый)"),
            new("Charon", "Charon — Мужской (Глубокий, солидный, низкий)"),
            new("Fenrir", "Fenrir — Мужской (Сильный, уверенный, брутальный)"),
            new("Sami", "Sami — Мужской (Спокойный, уверенный, низкий)"),
            new("Gacrux", "Gacrux — Мужской (Зрелый, рассудительный)"),
            new("Jori", "Jori — Мужской (Тёплый, располагающий к себе)"),
            new("Zephyr", "Zephyr — Мужской (Светлый, энергичный)"),
            new("Achernar", "Achernar — Мужской (Мягкий, интеллигентный)"),
            new("Callirrhoe", "Callirrhoe — Мужской (Непринуждённый, разговорный)"),
            new("Sulafat", "Sulafat — Мужской (Тёплый, бархатистый)")
        };

        // 🎭 ЭМОЦИОНАЛЬНЫЕ СТИЛИ РЕЧИ (ИЗ СКРИНШОТОВ 1 И 2)
        public static readonly List<StyleOption> Styles = new()
        {
            new("Whisper", "Whisper (Шёпот — тихая, приватная речь)"),
            new("Intimate", "Intimate (Интимный — чувственный, соблазнительный, близкий)"),
            new("Friendly", "Friendly (Дружелюбный — тёплая естественная подача)"),
            new("Promote", "Promote (Рекламный — бодрый, драйвовый, высокая энергия)"),
            new("Narration", "Narration (Повествовательный — выразительная модуляция, паузы)"),
            new("Podcast", "Podcast (Подкаст — спокойная, размеренная беседа)"),
            new("Empathetic", "Empathetic (Чуткий — заботливый, поддерживающий)"),
            new("Energetic", "Energetic (Энергичный — задорный, эмоциональный)"),
            new("Gravelly", "Gravelly (С хрипотцой — глубокий, сексуальный тембр)"),
            new("Smooth", "Smooth (Обволакивающий — бархатный, скользящий)"),
            new("Soft", "Soft (Нежный — ласковый, мягкий)"),
            new("Resonant", "Resonant (Глубокий — сочный, наполненный)"),
            new("Firm", "Firm (Твёрдый — уверенный, убедительный)"),
            new("Informative", "Informative (Дикторский — понятный, нейтральный)"),
            new("Even", "Even (Ровный — сдержанный, без эмоциональных всплесков)")
        };

        // ТЕМП
        public static readonly Dictionary<string, string> PaceMap = new()
        {
            { "Natural", "Natural (Естественный разговорный темп)" },
            { "The Drift", "The Drift (Медленный, томный, с длинными паузами на вдох)" },
            { "Rapid Fire", "Rapid Fire (Быстрый, энергичный, напористый)" },
            { "Staccato", "Staccato (Чёткий, отрывистый, акцентированный)" }
        };

        // АКЦЕНТЫ
        public static readonly Dictionary<string, string> Accents = new()
        {
            { "Neutral", "Нейтральный (Чистая русская/международная речь)" },
            { "American (Gen)", "Американский (Легкий непринужденный акцент)" },
            { "British (RP)", "Британский (Аристократичный, изысканный)" },
            { "French", "Французский (Чувственный, с мягким шармом)" },
            { "Slavic", "Славянский / Восточноевропейский" }
        };
    }

    public record VoiceOption(string Id, string Label);
    public record StyleOption(string Id, string Label);
}