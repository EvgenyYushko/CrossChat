using CrossChat.Integrations.Interfaces;

namespace CrossChat.Services.Base
{
	public class ConsoleServiceBase
	{
		private int? _userId = null;
		private int? _botId = null;
		private string _prodider;
		private readonly IUserConsoleService _consoleService;
		private readonly ILogger<ConsoleServiceBase> _logger;

		public ConsoleServiceBase(IUserConsoleService consoleService, ILogger<ConsoleServiceBase> logger, string provider)
		{
			_consoleService = consoleService;
			_logger = logger;
			_prodider = provider;
		}

		public void Init(int userId, int botId)
		{
			_userId = userId;
			_botId = botId;
		}

		public Task Log(string message, int? userId = null, int? botId = null)
		{
			var user = userId ?? _userId;
			var bot = botId ?? _botId;
			_logger.LogInformation($"[{_prodider}] {message}");
			return _consoleService.WriteLogAsync(user.Value, _prodider, bot.Value, message, _prodider);
		}

		public Task LogInfo(string message, int? userId = null, int? botId = null)
		{
			var user = userId ?? _userId;
			var bot = botId ?? _botId;
			_logger.LogInformation($"[{_prodider}] {message}");
			return _consoleService.WriteLogAsync(user.Value, _prodider, bot.Value, message, "info");
		}

		public Task LogWarning(string message, int? userId = null, int? botId = null)
		{
			var user = userId ?? _userId;
			var bot = botId ?? _botId;
			_logger.LogWarning($"[{_prodider}] {message}");
			return _consoleService.WriteLogAsync(user.Value, _prodider, bot.Value, message, "warning");
		}

		public Task LogError(string message, int? userId = null, int? botId = null)
		{
			var user = userId ?? _userId;
			var bot = botId ?? _botId;
			_logger.LogError($"[{_prodider}] {message}");
			return _consoleService.WriteLogAsync(user.Value, _prodider, bot.Value, message, "error");
		}
	}
}
