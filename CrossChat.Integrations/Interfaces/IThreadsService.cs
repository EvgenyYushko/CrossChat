using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Threads;

namespace CrossChat.Integrations.Interfaces;

public interface IThreadsService
{
	Task<ThreadsUserProfile?> GetThreadsUserProfileAsync(string accessToken);
	Task<(string NewToken, int ExpiresIn)?> RefreshTokenAsync(string currentToken);

	Task ReplyToThreadAsync(string targetMediaId, string text, string accessToken);

	Task<List<ThreadsItem>> GetUserThreadsAsync(string accessToken);

	Task<List<ThreadsItem>> GetConversationAsync(string mediaId, string accessToken);
	Task<string> CreateReplyContainerAsync(string targetMediaId, string text, string accessToken);
	Task PublishReplyAsync(string creationId, string accessToken);

	Task<bool> WaitForMediaReadyAsync(string containerId, string accessToken, int maxWaitSeconds = 60);

	Task<string> GetContainerStatusAsync(string containerId, string accessToken);

	Task<(bool Success, string? PostId)> CreatePostAsync(string caption, List<string> imagesBase64, string accessToken);
	Task<string?> CreateReplyAsync(string replyToPostId, string text, string accessToken);

	Task<ThreadsFeedPageDto> GetAccountFeedAsync(string accessToken, int limit = 12, string? after = null, string? before = null);
	Task<ThreadsPostInsightsDto> GetThreadInsightsAsync(string threadMediaId, string accessToken);
	Task<ThreadsAccountInsightsDto> GetAccountInsightsAsync(string accessToken);
}

public record ThreadsUserProfile(string Id, string Username, string? ProfilePictureUrl);

