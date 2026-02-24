using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IInstagramService
	{
		/// <summary>
		/// Получить последние сообщения с пользователем
		/// </summary>
		/// <param name="userId">По кому запрашиваем историю</param>
		/// <param name="limit">колличество сообщений</param>
		/// <returns></returns>
		Task<List<MessageItem>> GetHistoryAsync(string userId, string accessToken, int limit = 10);

		/// <summary>
		/// Отправить сообщения
		/// </summary>
		/// <param name="recipientId">Кому отправить</param>
		/// <param name="text">Текст</param>
		/// <returns></returns>
		Task SendMessageAsync(string recipientId, string text, string accessToken);

		/// <summary>
		/// Показать индикатор набора сообщения
		/// </summary>
		/// <param name="recipientId">Кому отображать</param>
		/// <param name="on">показать?</param>
		/// <returns></returns>
		Task SetTypingStatusAsync(string recipientId, string accessToken, bool on = true);

		/// <summary>
		/// Поставить лайк на сообщение
		/// </summary>
		/// <param name="recipientId">Кому ставим</param>
		/// <param name="messageId">id сообщения</param>
		/// <returns></returns>
		Task SendReactionAsync(string recipientId, string messageId, string accessToken);
	}
}
