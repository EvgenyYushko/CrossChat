namespace CrossChat.Worker.Contracts;

public record BlueSkyProcessReply
{
	public int BotDbId { get; init; }      // ID записи в нашей базе
	public string ConvoId { get; init; } = string.Empty; // ID чата в BlueSky
}
