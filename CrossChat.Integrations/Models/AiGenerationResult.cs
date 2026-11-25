namespace CrossChat.Integrations.Models;

public class AiGenerationResult
{
	public string Text { get; set; } = string.Empty;
	public int InputTokens { get; set; }
	public int OutputTokens { get; set; }
	public int ThoughtTokens { get; set; }
	public int TotalTokens { get; set; }
	public string ModelName { get; set; } = string.Empty;
}