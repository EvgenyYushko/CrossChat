using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class InstagramConsole : ConsoleServiceBase, IInstagramConsole
	{
		private const string PROVIDER = "instagram";

		public InstagramConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
