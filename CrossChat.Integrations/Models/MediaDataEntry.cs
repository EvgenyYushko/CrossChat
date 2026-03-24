namespace CrossChat.Integrations.Models;
public class MediaDataEntry
{
	public string Url { get; set; }
	public string AiResult { get; set; } // Распознанный текст
	public string MediaType { get; set; }
	public bool IsProcessed { get; set; }    // Флаг: false - только ссылка, true - текст готов
}
