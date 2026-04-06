namespace CrossChat.Integrations.Interfaces;
public interface IThreadsService
{
	Task<(string NewToken, int ExpiresIn)?> RefreshTokenAsync(string currentToken);

	Task ReplyToThreadAsync(string targetMediaId, string text, string accessToken);
}
