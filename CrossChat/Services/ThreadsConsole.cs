using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class ThreadsConsole: ConsoleServiceBase, IThreadsConsole
	{
		private const string PROVIDER = "threads";

		public ThreadsConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
