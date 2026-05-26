namespace CrossChat.Integrations.Interfaces
{
	public interface IInstagramConsoleService
	{
		public Task WriteInfo(int userId, int botId, string message);

		public Task WriteError(int userId, int botId, string message);
	}
}
