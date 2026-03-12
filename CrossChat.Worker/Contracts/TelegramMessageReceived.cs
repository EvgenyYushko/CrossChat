namespace CrossChat.Worker.Contracts
{
	public class TelegramMessageReceived
	{
		public string BotToken { get; set; }
		public long ChatId { get; set; }
		public string Text { get; set; }
	}
}
