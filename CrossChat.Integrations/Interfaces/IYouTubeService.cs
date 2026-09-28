using System.Threading.Tasks;

namespace CrossChat.Integrations.Interfaces
{
	public interface IYouTubeService
	{
		string GetAuthorizationUrl(string state, string redirectUri);
		Task<(string AccessToken, string? RefreshToken, int ExpiresIn)?> ExchangeCodeForTokensAsync(string code, string redirectUri);
		Task<(string? AccessToken, int ExpiresIn)?> RefreshAccessTokenAsync(string refreshToken);
		Task<YouTubeChannelInfoDto?> GetChannelInfoAsync(string accessToken);

		Task<(bool Success, string? VideoId, string? ErrorMessage)> UploadVideoAsync(
			byte[] videoBytes,
			string title,
			string description,
			List<string> tags,
			string accessToken);
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
}