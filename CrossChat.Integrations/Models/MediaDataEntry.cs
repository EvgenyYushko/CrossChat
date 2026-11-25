namespace CrossChat.Integrations.Models;
public class MediaDataEntry
{
	public string Url { get; set; }
	public string AiResult { get; set; } // Распознанный текст
	public string MediaType { get; set; }
	public bool IsProcessed { get; set; }    // Флаг: false - только ссылка, true - текст готов
	public int InputTokens { get; set; }
	public int OutputTokens { get; set; }
	public int TotalTokens { get; set; }
	public string? ModelName { get; set; }
}
