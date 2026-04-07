namespace CrossChat.Worker.Contracts;

public record ThreadsProcessReply
{
	public int BotId { get; init; }           // Наш внутренний ID настроек бота в БД
	public string ThreadsUserId { get; init; }          
	public string TargetMediaId { get; init; } // ID комментария, на который отвечаем
	public string UserText { get; init; } = string.Empty;
	public string Username { get; init; } = string.Empty;
}
