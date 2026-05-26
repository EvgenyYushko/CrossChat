using CrossChat.Hubs;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace CrossChat.Services
{
	public class UserConsoleService : IUserConsoleService
	{
		private readonly IHubContext<LogHub> _hubContext;

		public UserConsoleService(IHubContext<LogHub> hubContext)
		{
			_hubContext = hubContext;
		}

		public async Task WriteLogAsync(int userId, string provider, int botId, string message, string type = "info")
		{
			var timestamp = DateTime.Now.ToString("HH:mm:ss");

			// Формируем имя изолированной комнаты
			var groupName = $"bot_room_{provider.ToLower()}_{botId}";

			// Шлем лог только тем, кто открыл консоль этого конкретного бота
			await _hubContext.Clients.Group(groupName)
				.SendAsync("ReceiveLog", timestamp, type.ToUpper(), message);
		}
	}
}
