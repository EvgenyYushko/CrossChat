using CrossChat.Integrations.Models;

namespace CrossChat.Integrations.Interfaces
{
	public interface IInboxService
	{
		// Получить или создать диалог (возвращает чистый DTO)
		Task<InboxConversationDto> GetOrCreateConversationAsync(
			int profileId,
			int networkType,
			int botId,
			string externalUserId,
			string? username,
			string? fullName,
			string? avatarUrl);

		// Сохранить входящее сообщение от клиента по Id диалога
		Task<bool> SaveIncomingMessageAsync(
			int conversationId,
			string externalMsgId,
			string text,
			string? mediaUrl = null,
			string? mediaType = null);

		// Сохранить ответ ИИ-бота по Id диалога
		Task<bool> SaveBotReplyAsync(
			int conversationId,
			string text);

		// Отправка сообщения менеджером руками через сайт
		Task<(bool Success, string? ErrorMessage)> SendManagerReplyAsync(
			int conversationId,
			string text);

		// Переключение паузы ИИ (Human Takeover)
		Task<bool> ToggleHumanTakeoverAsync(
			int conversationId,
			bool isTakeover);

		Task<int> SyncProfileConversationsAsync(int profileId);

		Task<bool> SaveEchoMessageAsync(
			int networkType,
			string businessAccountId,
			string customerId,
			string messageId,
			string text);

		Task<bool> SendMessageReactionAsync(int messageId, string reaction = "love");
	}
}