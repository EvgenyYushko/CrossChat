namespace CrossChat.Worker.Contracts;

public record ProcessMediaCommand
{
	public string MessageId { get; set; } = string.Empty;
	public string Url { get; set; } = string.Empty;
	public string MediaType { get; set; } = string.Empty; // "image" или "video"

	public string SenderId { get; set; } 
    public string RecipientId { get; set; } 
}
