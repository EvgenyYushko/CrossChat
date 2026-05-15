using CrossChat.Integrations.Models;
using WTelegram;

namespace CrossChat.Integrations.Interfaces
{
	public interface ITelegramUserBotService
	{
		Task<Client> CreateAndConnectAsync(UserBotDto dto);
		Task<byte[]> GetSessionBytesAsync(int botId);
	}
}
