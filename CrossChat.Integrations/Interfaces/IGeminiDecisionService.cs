namespace CrossChat.Integrations.Interfaces
{
    public interface IGeminiDecisionService
    {
        Task<DirectReplyDecisionResult> EvaluateDirectReplyAsync(string conversationTranscript, CancellationToken ct = default);
    }

    public class DirectReplyDecisionResult
    {
        public bool IsSuccess { get; set; }
        public double NeedsReplyProb { get; set; } = 1.0;       // По умолчанию true (Fail-Open)
        public double NeedsReplyConfidence { get; set; } = 1.0;
        public double IsSpamProb { get; set; } = 0.0;           // По умолчанию false
        public double IsSpamConfidence { get; set; } = 1.0;
        public int InputTokens { get; set; }
        public long ElapsedMs { get; set; }
        public string? ErrorMessage { get; set; }
    }
}