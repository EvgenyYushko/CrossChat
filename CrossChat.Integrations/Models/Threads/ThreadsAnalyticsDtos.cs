namespace CrossChat.Integrations.Models.Threads
{
	public class ThreadsFeedPostDto
	{
		public string Id { get; set; } = string.Empty;
		public string? Text { get; set; }
		public string MediaType { get; set; } = "TEXT_POST"; // TEXT_POST, IMAGE, VIDEO, CAROUSEL_ALBUM
		public string? MediaUrl { get; set; }
		public string? ThumbnailUrl { get; set; }
		public string? Permalink { get; set; }
		public DateTime Timestamp { get; set; }

		// Метрики вовлеченности
		public int Views { get; set; }
		public int Likes { get; set; }
		public int Replies { get; set; }
		public int Reposts { get; set; }
		public int Quotes { get; set; }
	}

	public class ThreadsFeedPageDto
	{
		public List<ThreadsFeedPostDto> Posts { get; set; } = new();
		public string? AfterCursor { get; set; }
		public string? BeforeCursor { get; set; }
		public bool HasNext => !string.IsNullOrEmpty(AfterCursor);
		public bool HasPrevious => !string.IsNullOrEmpty(BeforeCursor);
	}

	public class ThreadsPostInsightsDto
	{
		public int Views { get; set; }
		public int Likes { get; set; }
		public int Replies { get; set; }
		public int Reposts { get; set; }
		public int Quotes { get; set; }
		public int TotalInteractions => Likes + Replies + Reposts + Quotes;

		// True ER = (Все реакции / Просмотры) * 100%
		public double EngagementRate { get; set; }
		public string EngagementBadge { get; set; } = "Норма";
		public string BadgeColor { get; set; } = "#10b981";
	}

	public class ThreadsAccountInsightsDto
	{
		public int Views { get; set; }
		public int Likes { get; set; }
		public int Replies { get; set; }
		public int Reposts { get; set; }
		public int Quotes { get; set; }
		public int FollowersCount { get; set; }
	}
}
