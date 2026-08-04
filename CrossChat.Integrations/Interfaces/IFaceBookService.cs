namespace CrossChat.Integrations.Interfaces
{
	public interface IFaceBookService
	{
		Task<FbUser> GetMeAsync(string token);
		Task<List<FbConversation>> GetUnreadDialogsAsync(string token, string pageId);
		Task<bool> SendReplyAsync(string recipientId, string text, string token);
		Task<FbConversation> GetDialogByIdAsync(string token, string dlgId);

		// Media part
		Task<bool> PublishToPageAsync(string message, string acessToken, string pageIdToPublish, List<string> base64Images = null);
		Task<bool> PublishStoryAsync(string base64Image, string acessToken, string pageIdToPublish);
		Task<bool> PublishReelAsync(string message, string base64Video, string acessToken, string pageIdToPublish);
	}

	public class FbUser
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public string ProfilePicUrl { get; set; }
	}

	public class FbConversationResponse
	{
		public List<FbConversation> data { get; set; }
	}

	public class FbConversation
	{
		public string id { get; set; }
		public int unread_count { get; set; }
		public FbMessageList messages { get; set; }
	}

	public class FbMessageList
	{
		public List<FbMessage> data { get; set; }
	}

	public class FbMessage
	{
		public string id { get; set; }
		public string message { get; set; }
		public FbFrom from { get; set; }
	}

	public class FbFrom
	{
		public string id { get; set; }
		public string name { get; set; }
	}

	public class ReelStartResponse
	{
		// ID контейнера видео (Reel)
		public string video_id { get; set; }
		// Специальный URL для загрузки файла
		public string upload_url { get; set; }
	}

	public class PageToken
	{
		// Токен, который нужен для публикации
		public string access_token { get; set; }
		// ID Страницы
		public string id { get; set; }
		// Имя Страницы
		public string name { get; set; }
	}

	public class AccountsResponse
	{
		// Список страниц находится в поле "data"
		public List<PageToken> data { get; set; }
	}

	public class PublishResponse
	{
		// Ожидаемый формат ID: "pageId_postId"
		public string id { get; set; }

		// ID поста, возвращаемый API Reels после шага "finish"
		public string post_id { get; set; }
	}

	public class UploadResponse
	{
		// ID загруженной фотографии (media_fbid)
		public string id { get; set; }
	}
}
