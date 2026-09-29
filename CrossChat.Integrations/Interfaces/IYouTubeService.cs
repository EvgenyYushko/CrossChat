namespace CrossChat.Integrations.Interfaces
{
	public interface IYouTubeService
	{
		string GetAuthorizationUrl(string state, string redirectUri);
		Task<(string AccessToken, string? RefreshToken, int ExpiresIn)?> ExchangeCodeForTokensAsync(string code, string redirectUri);
		Task<(string? AccessToken, int ExpiresIn)?> RefreshAccessTokenAsync(string refreshToken);
		Task<YouTubeChannelInfoDto?> GetChannelInfoAsync(string accessToken);

		// Загрузка видео с выбором статуса приватности (public, unlisted, private)
		Task<(bool Success, string? VideoId, string? ErrorMessage)> UploadVideoAsync(
			byte[] videoBytes,
			string title,
			string description,
			List<string> tags,
			string privacyStatus,
			string accessToken);

		// Загрузка кастомной обложки (Custom Thumbnail)
		Task<bool> SetThumbnailAsync(string videoId, byte[] imageBytes, string accessToken);

		// Публикация первого комментария под видео
		Task<bool> AddCommentAsync(string videoId, string commentText, string accessToken);

		Task<List<YouTubeCommentDto>> GetRecentCommentsAsync(string channelId, string accessToken, int maxResults = 20);
		Task<bool> ReplyToCommentAsync(string parentCommentId, string replyText, string accessToken);
	}

	public class YouTubeChannelInfoDto
	{
		public string ChannelId { get; set; } = string.Empty;
		public string Title { get; set; } = string.Empty;
		public string? CustomUrl { get; set; }
		public string? AvatarUrl { get; set; }
		public ulong SubscriberCount { get; set; }
		public ulong VideoCount { get; set; }
	}

	public class YouTubeCommentDto
	{
		public string CommentId { get; set; } = string.Empty;
		public string VideoId { get; set; } = string.Empty;
		public string AuthorDisplayName { get; set; } = string.Empty;
		public string? AuthorChannelId { get; set; }
		public string Text { get; set; } = string.Empty;
		public DateTime PublishedAt { get; set; }
	}
}