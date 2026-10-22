using CrossChat.Integrations.Models;
using Microsoft.AspNetCore.Http;

namespace CrossChat.Integrations.Interfaces
{
	public interface IInboxService
	{
		// 1. Получить или создать диалог (возвращает чистый DTO)
		Task<InboxConversationDto> GetOrCreateConversationAsync(
			int profileId,
			int networkType,
			int botId,
			string externalUserId,
			string? username,
			string? fullName,
			string? avatarUrl);

		// 2. Сохранить входящее сообщение от клиента по Id диалога
		Task<bool> SaveIncomingMessageAsync(
			int conversationId,
			string externalMsgId,
			string text,
			string? mediaUrl = null,
			string? mediaType = null);

		// 3. Сохранить ответ ИИ-бота по Id диалога
		Task<bool> SaveBotReplyAsync(
			int conversationId,
			string text);

		// 4. Отправка сообщения менеджером руками через сайт
		Task<(bool Success, string? ErrorMessage)> SendManagerReplyAsync(
			int conversationId,
			string text);

		// 5. Переключение паузы ИИ (Human Takeover)
		Task<bool> ToggleHumanTakeoverAsync(
			int conversationId,
			bool isTakeover);

		// 6. Синхронизация диалогов профиля (Instagram + Facebook)
		Task<int> SyncProfileConversationsAsync(int profileId);

		// 7. Обработка исходящего эхо-сообщения (когда ответили с телефона)
		Task<bool> SaveEchoMessageAsync(
			int networkType,
			string businessAccountId,
			string customerId,
			string messageId,
			string text,
			string? mediaUrl = null,
			string? mediaType = null);

		// 8. Отправка реакции на сообщение
		Task<bool> SendMessageReactionAsync(int messageId, string reaction = "love");

		// 9. Переключение реакции (поставить или снять лайк)
		Task<(bool Success, string? NewReaction)> ToggleMessageReactionAsync(int messageId);

		// 10. Обработка реакции из вебхука (когда лайк поставили/сняли на телефоне)
		Task<bool> HandleReactionWebhookAsync(string businessAccountId, string externalMessageId, string? emoji, string? action);

		// 11. Удаление сообщения по внешнему ID (из вебхука при Unsend)
		Task<bool> DeleteMessageByExternalIdAsync(string externalMessageId);

		// 12. Удаление сообщения менеджером с сайта (по ID из БД)
		Task<bool> DeleteMessageFromInboxAsync(int messageId);

		// 13. Отправка медиафайла менеджером (фото, видео, голосовые)
		Task<(bool Success, string? ErrorMessage)> SendManagerMediaReplyAsync(int conversationId, IFormFile file);
	}
}