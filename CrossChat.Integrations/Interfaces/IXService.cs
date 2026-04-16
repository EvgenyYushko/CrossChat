namespace CrossChat.Integrations.Interfaces
{
	public interface IXService
	{
		Task<bool> CreateTextPostAsync(string text, string accessToken);
	}
}
