namespace CrossChat.Integrations.Models
{
    public class VoiceGenerationRequest
    {
        public string Text { get; set; } = string.Empty;
        public string ProfileName { get; set; } = "Creator";
        public string VoiceName { get; set; } = "Aoede";
        public string Style { get; set; } = "Whisper";
        public string Pace { get; set; } = "Natural";
        public string Accent { get; set; } = "American (Gen)";
        public string Scene { get; set; } = string.Empty;
        public string? SampleContext { get; set; }
    }
}