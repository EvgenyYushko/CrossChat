using CrossChat.Integrations.Models;
using static CrossChat.Integrations.Services.InstagramService;

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
		/// <returns>result - True/ False</returns>
		Task<bool> SendReactionAsync(string recipientId, string messageId, string reaction, string accessToken);

		/// <summary>
		/// Получить рандомную реакцию
		/// </summary>
		/// <param name="allowedReactions"></param>
		/// <returns></returns>
		string GetRandomReaction(string allowedReactions);

		/// <summary>
		/// Получить информацию о профиле пользователя
		/// </summary>
		/// <param name="userId">Id польователя инсты</param>
		Task<InstagramUserProfile> GetInstagramUserProfileAsync(string userId, string accessToken);

		/// <summary>
		/// Обновить токен
		/// </summary>
		/// <param name="currentToken">текущий ещё дивой токен</param>
		Task<(string NewToken, int ExpiresIn)?> RefreshTokenAsync(string currentToken);

		Task<(string? username, string? instagramScopedUserId, string? profilePicUrl)> GetMeInfo(string? accessToken);

		Task<string?> CreateCommentAsync(string mediaId, string text, string accessToken);

		Task<string> ProcessAndCacheMediaAsync(MediaDataEntry media, string messageId);

		Task<string> GetUserContextForAiAsync(string userId, string accessToken);


		// Media part
		Task<CreateMediaResult> CreateMediaAsync(List<string> base64Strings, string accessToken, string caption, string locationId);

		Task<string> PublishStoryFromBase64(string base64Img, string accessToken);

		Task<List<InstagramMedia>> GetUserMediaAsync(string accessToken, int limit = 50);
		Task<DailyStoryResult> PublishDailyStoryAsync(InstagramDailyStoryDto dto);

		Task<InstagramFeedPageDto> GetAccountFeedAsync(string accessToken, int limit = 12, string? after = null, string? before = null);
		Task<InstagramPostInsightsDto> GetMediaInsightsAsync(string mediaId, string mediaType, string accessToken);

		Task<InstagramAccountInsightsDto> GetAccountInsightsAsync(string accessToken);

		Task<List<InstagramConversationSummaryDto>> GetRecentConversationsAsync(string accessToken, string businessAccountId, int limit = 15);

		Task<string?> SendMediaAttachmentAsync(string recipientId, string mediaType, string mediaUrl, string accessToken);

		// === КОММЕНТАРИИ ПОСТОВ INSTAGRAM ===

		/// <summary>
		/// Получает дерево комментариев поста с вложенными ответами (Replies)
		/// </summary>
		Task<List<InstagramCommentItemDto>> GetMediaCommentsAsync(string mediaId, string accessToken);

		
		/// <summary>
		/// Отправляет ответ на конкретный комментарий и возвращает ID созданного ответа
		/// </summary>
		Task<string?> ReplyToCommentAsync(string commentId, string text, string accessToken);

		/// <summary>
		/// Удаляет комментарий или ответ в Instagram (для модерации спама)
		/// </summary>
		Task<bool> DeleteCommentAsync(string commentId, string accessToken);
	}
}
