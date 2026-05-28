using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class BlueSkyConsole: ConsoleServiceBase, IBlueSkyConsole
	{
		private const string PROVIDER = "bluesky";

		public BlueSkyConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
