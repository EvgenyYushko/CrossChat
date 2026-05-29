namespace CrossChat.Worker.Contracts;

// 1. Событие: "Пришел вебхук Threads"
public record ThreadsEventReceived
{
	public string BotThreadsId { get; init; } = string.Empty; // Кому пришло (ID Ланы)
	public string Type { get; init; } = string.Empty;        // "replies" или "mentions"
	public string MediaId { get; init; } = string.Empty;      // ID коммента, на который отвечаем
	public string Text { get; init; } = string.Empty;         // Что написал юзер
	public string Username { get; init; } = string.Empty;     // Кто написал
}

// 2. Команда: "Опубликовать готовый ответ"
public record PublishThreadsCommand
{
	public int BotDbId { get; init; }             // ID настроек в нашей БД
	public string CreationId { get; init; } = string.Empty; // ID контейнера от Meta
	public string TargetMediaId { get; init; } = string.Empty; // Для логов
	public string Username { get; init; } = string.Empty;
}
