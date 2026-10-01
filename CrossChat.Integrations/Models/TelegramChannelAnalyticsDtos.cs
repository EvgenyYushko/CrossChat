namespace CrossChat.Integrations.Models
{
	public class TelegramChannelPostDto
	{
		public string MessageId { get; set; } = string.Empty;
		public string? Text { get; set; }
		public DateTime Timestamp { get; set; }
		public int Views { get; set; }
		public int Reactions { get; set; }
		public string MediaType { get; set; } = "TEXT"; // TEXT, PHOTO, VIDEO
		public string? MediaUrl { get; set; }
		public string PostUrl { get; set; } = string.Empty;
		public double EngagementRate { get; set; }
	}

	public class TelegramChannelStatsDto
	{
		public long ChannelId { get; set; }
		public string ChannelTitle { get; set; } = string.Empty;
		public string? ChannelUsername { get; set; }
		public string? ProfilePictureUrl { get; set; }
		public bool IsPublic => !string.IsNullOrEmpty(ChannelUsername);

		// Живые метрики из Telegram Bot API
		public int SubscribersCount { get; set; }
		public bool AutoApproveJoinRequests { get; set; }

		// Метрики публичной витрины (если публичный)
		public int TotalViewsOnFeed { get; set; }
		public int TotalReactionsOnFeed { get; set; }
		public double AverageEr { get; set; }
	}

	public class TelegramChannelAnalyticsPageDto
	{
		public TelegramChannelStatsDto Stats { get; set; } = new();
		public List<TelegramChannelPostDto> Posts { get; set; } = new();
	}
}
