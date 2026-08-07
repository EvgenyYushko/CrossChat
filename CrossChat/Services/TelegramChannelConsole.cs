using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class TelegramChannelConsole: ConsoleServiceBase, ITelegramChannelConsole
	{
		private const string PROVIDER = "telegramchannel";

		public TelegramChannelConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
