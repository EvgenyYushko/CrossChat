using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class XConsole: ConsoleService, IXConsole
	{
		private const string PROVIDER = "x";

		public XConsole(IUserConsoleService consoleService, ILogger<ConsoleService> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
