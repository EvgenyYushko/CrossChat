using CrossChat.Integrations.Interfaces;

namespace CrossChat.Services
{
	public class InstagramConsoleService : IInstagramConsoleService
	{
		private readonly IUserConsoleService _consoleService;

		public InstagramConsoleService(IUserConsoleService consoleService)
		{
			_consoleService = consoleService;
		}

		public Task WriteInfo(int userId, int botId, string message)
		{
			return _consoleService.WriteLogAsync(userId, "instagram", botId, message, "info");
		}

		public Task WriteError(int userId, int botId, string message)
		{
			return _consoleService.WriteLogAsync(userId, "instagram", botId, message, "error");
		}
	}
}
