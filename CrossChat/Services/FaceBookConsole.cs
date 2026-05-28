using CrossChat.Integrations.Interfaces;
using CrossChat.Services.Base;

namespace CrossChat.Services
{
	public class FaceBookConsole : ConsoleServiceBase, IFaceBookConsole
	{
		private const string PROVIDER = "facebook";

		public FaceBookConsole(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger)
			: base(consoleService, logger, PROVIDER)
		{
		}
	}
}
