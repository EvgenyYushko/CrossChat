namespace CrossChat.Integrations.Interfaces
{
	public interface ITelegramService
	{
		Task SetWebhookAsync(string token, string webhookUrl);
		Task DeleteWebhookAsync(string token);
		Task SendMessageAsync(string token, long chatId, string text);
	}
}
