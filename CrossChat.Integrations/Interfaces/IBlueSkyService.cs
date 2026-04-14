using CrossChat.Integrations.Services;

namespace CrossChat.Integrations.Interfaces;

public interface IBlueSkyService
{
	Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string privateKeyJson);
	(string proof, string privateKeyJson) CreateDPoPProof(string method, string url, string? existingKeyJson = null, string? nonce = null, string? accessToken = null, string? aud = null);

	Task<string> GetValidTokenAsync(BlueSkyModel settings);

	Task<List<Convo>> GetUnreadConversationsAsync(BlueSkyModel settings);
	Task<bool> SendChatMessageAsync(BlueSkyModel settings, string convoId, string text);

	Task MarkConvoAsReadAsync(BlueSkyModel settings, string convoId, string lastMessageId);
}
