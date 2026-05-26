using CrossChat.Hubs;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace CrossChat.Services
{
	public class UserConsoleService : IUserConsoleService
	{
		private readonly IHubContext<LogHub> _hubContext;
		IDatabase _redis;

		public UserConsoleService(IHubContext<LogHub> hubContext, IConnectionMultiplexer redisMux)
		{
			_hubContext = hubContext;
			_redis = redisMux.GetDatabase();
		}

		public async Task WriteLogAsync(int userId, string provider, int botId, string message, string type = "info")
		{
			var timestamp = DateTime.Now.ToString("HH:mm:ss");
			var groupName = $"bot_room_{provider.ToLower()}_{botId}";
			var logEntry = $"[{timestamp}] [{type.ToUpper()}] {message}";

			// 1. Отправляем в реальном времени (SignalR)
			await _hubContext.Clients.Group(groupName).SendAsync("ReceiveLog", timestamp, type.ToUpper(), message);

			// 2. СОХРАНЯЕМ В ХИСТОРИ (Redis)
			var historyKey = $"log_history:{provider.ToLower()}:{botId}";

			// Добавляем запись в конец списка
			await _redis.ListRightPushAsync(historyKey, logEntry);

			// Ограничиваем список, например, последними 100 записями, чтобы не забить память
			await _redis.ListTrimAsync(historyKey, -100, -1);

			// Ставим время жизни истории — 24 часа (чтобы старые боты не занимали место)
			await _redis.KeyExpireAsync(historyKey, TimeSpan.FromHours(24));
		}
	}
}
