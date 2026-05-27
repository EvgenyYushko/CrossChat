using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class FaceBookConsole : ConsoleService, IFaceBookConsole
	{
		private const string PROVIDER = "facebook";

		public FaceBookConsole(IUserConsoleService consoleService, ILogger<ConsoleService> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
