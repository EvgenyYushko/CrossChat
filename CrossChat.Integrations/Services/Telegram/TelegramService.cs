using CrossChat.Integrations.Interfaces;
using Telegram.Bot;

namespace CrossChat.Integrations.Services.Telegram
{
	public class TelegramService : ITelegramService
	{
		public TelegramService()
		{
			
		}
		public async Task SetWebhookAsync(string token, string webhookUrl)
		{
			var bot = new TelegramBotClient(token);
			await bot.SetWebhook(webhookUrl);
		}

		public async Task DeleteWebhookAsync(string token)
		{
			var bot = new TelegramBotClient(token);
			await bot.DeleteWebhook();
		}

		public async Task SendMessageAsync(string token, long chatId, string text)
		{
			var bot = new TelegramBotClient(token);
			await bot.SendMessage(chatId, text);
		}
	}
}
