using System.Collections.Concurrent;
using WTelegram;

namespace CrossChat.Integrations.Services
{
	public class TelegramUserBotRegistry
	{
		// Храним работающие клиенты по их ID из базы данных
		public ConcurrentDictionary<int, Client> ActiveClients { get; } = new();
	}
}
