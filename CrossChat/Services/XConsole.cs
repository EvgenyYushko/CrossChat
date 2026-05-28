using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class XConsole: ConsoleServiceBase, IXConsole
	{
		private const string PROVIDER = "x";

		public XConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
