namespace CrossChat.Integrations.Interfaces
{
	public interface IInstagramConsole : IConsoleService{}
	public interface IFaceBookConsole : IConsoleService{}
	public interface IThreadsConsole : IConsoleService{}
	public interface IXConsole : IConsoleService{}
	public interface IBlueSkyConsole : IConsoleService{}
	public interface ITelegramChannelConsole : IConsoleService{}
	
	public interface IConsoleService
	{
		public void Init(int userId, int botId);
		public Task Log(string message, int? userId = null, int? botId = null);
		public Task LogInfo(string message, int? userId = null, int? botId = null);
		public Task LogWarning(string message, int? userId = null, int? botId = null);
		public Task LogError(string message, int? userId = null, int? botId = null);
	}
}
