namespace CrossChat.Integrations.Interfaces
{
	public interface IXService
	{
		Task<bool> CreateTextPostAsync(string text, string accessToken);

		Task<(string AccessToken, string RefreshToken, int ExpiresIn)?> RefreshTokenAsync(string refreshToken, string xClientId, string xClientSecret);
	}
}
