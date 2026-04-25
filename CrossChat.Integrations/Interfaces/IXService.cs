namespace CrossChat.Integrations.Interfaces
{
	public record XUserProfile(string Id, string Username, string? ProfilePictureUrl);
	public interface IXService
	{
		Task<XUserProfile?> GetXUserProfileAsync(string accessToken);

		Task<bool> CreateTextPostAsync(string text, string accessToken);

		Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string xClientId, string xClientSecret);
	}
}
