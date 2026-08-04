namespace CrossChat.Integrations.Interfaces
{
	public record XUserProfile(string Id, string Username, string? ProfilePictureUrl);
	public interface IXService
	{
		Task<XUserProfile?> GetXUserProfileAsync(string accessToken);

		Task<bool> CreateTextPostAsync(string text, string accessToken);

		Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string xClientId, string xClientSecret);

		// MediaPart
		Task<bool> CreateImagePost(string caption, List<string> base64Files, string accessToken);

		Task<bool> CreateVideoPost(string caption, string base64Video, string accessToken);
	}
}
