using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class ThreadsConsole: ConsoleService, IThreadsConsole
	{
		private const string PROVIDER = "threads";

		public ThreadsConsole(IUserConsoleService consoleService, ILogger<ConsoleService> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
