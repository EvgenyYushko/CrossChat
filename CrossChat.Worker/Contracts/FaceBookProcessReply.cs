namespace CrossChat.Worker.Contracts
{
	public class FaceBookProcessReply
	{
		public int BotDbId { get; init; }
		public string DialogId { get; init; } = string.Empty;
	}
}
