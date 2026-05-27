using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class InstagramConsole : ConsoleService, IInstagramConsole
	{
		private const string PROVIDER = "instagram";

		public InstagramConsole(IUserConsoleService consoleService, ILogger<ConsoleService> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
