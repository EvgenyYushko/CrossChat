using CrossChat.Integrations.Services;

namespace CrossChat.Integrations.Interfaces;

public interface IBlueSkyService
{
	Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string privateKeyJson);
	(string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null, string? accessToken = null, string? aud = null);

	Task<string> GetValidTokenAsync(BlueSkyModel settings);

	Task<List<MessageBlueSky>> GetMessagesAsync(BlueSkyModel settings, string convoId, int limit = 15);
	Task<List<Convo>> GetUnreadConversationsAsync(BlueSkyModel settings);
	Task<bool> SendChatMessageAsync(BlueSkyModel settings, string convoId, string text);

	Task MarkConvoAsReadAsync(BlueSkyModel settings, string convoId, string lastMessageId);

	// MediaPart
	Task<Blob?> UploadImageFromBase64Async(string base64Image, string mimeType, BlueSkyModel setting);
	Task<string> TruncateTextToMaxLength(string text);
	Task<bool> CreatePostWithImagesAsync(string postText, List<ImageAttachment> images, BlueSkyModel setting);
	Task<bool> CreatePostAsync(string postText, BlueSkyModel setting);
	Task<bool> PublishPostWithImagesAsync(string caption, List<string> base64Images, BlueSkyModel settings);
}
