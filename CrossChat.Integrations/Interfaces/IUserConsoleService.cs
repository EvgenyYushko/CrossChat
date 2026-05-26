namespace CrossChat.Integrations.Interfaces
{
	public interface IUserConsoleService
	{
		// Отправить лог конкретному пользователю
		// type может быть: "info", "gemini", "instagram", "telegram", "error"
		Task WriteLogAsync(int userId, string provider, int botId, string message, string type = "info");
	}
}
