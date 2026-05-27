using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class BlueSkyConsole: ConsoleService, IBlueSkyConsole
	{
		private const string PROVIDER = "bluesky";

		public BlueSkyConsole(IUserConsoleService consoleService, ILogger<ConsoleService> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
