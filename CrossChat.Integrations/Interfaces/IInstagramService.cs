using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IInstagramService
	{
		// Найти ID диалога и получить последние сообщения
		Task<List<MessageItem>> GetHistoryAsync(string userId, string accessToken, int limit = 10);

		// Отправить сообщение
		Task SendMessageAsync(string recipientId, string text, string accessToken);

		Task SetTypingStatusAsync(string recipientId, string accessToken, bool on = true);
	}
}
