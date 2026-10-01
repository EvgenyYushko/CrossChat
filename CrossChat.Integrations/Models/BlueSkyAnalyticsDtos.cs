namespace CrossChat.Integrations.Models
{
	public class BlueSkyFeedPostDto
	{
		public string Uri { get; set; } = string.Empty;
		public string Cid { get; set; } = string.Empty;
		public string? Text { get; set; }
		public string MediaType { get; set; } = "TEXT"; // TEXT, IMAGE, VIDEO
		public string? MediaUrl { get; set; }
		public string? ThumbnailUrl { get; set; }
		public string? Permalink { get; set; }
		public DateTime Timestamp { get; set; }

		// Метрики (в AT Protocol приходят сразу в объекте поста!)
		public int LikesCount { get; set; }
		public int RepostsCount { get; set; }
		public int RepliesCount { get; set; }
		public int QuotesCount { get; set; }
	}

	public class BlueSkyFeedPageDto
	{
		public List<BlueSkyFeedPostDto> Posts { get; set; } = new();
		public string? Cursor { get; set; }
		public bool HasNext => !string.IsNullOrEmpty(Cursor);
	}

	public class BlueSkyFullProfileDto
	{
		public string Did { get; set; } = string.Empty;
		public string Handle { get; set; } = string.Empty;
		public string? DisplayName { get; set; }
		public string? AvatarUrl { get; set; }
		public int FollowersCount { get; set; }
		public int FollowsCount { get; set; }
		public int PostsCount { get; set; }
	}

	public class BlueSkyAccountInsightsDto
	{
		public int FollowersCount { get; set; }
		public int FollowsCount { get; set; }
		public int PostsCount { get; set; }

		// Суммарная активность по ленте
		public int TotalLikesOnFeed { get; set; }
		public int TotalRepostsOnFeed { get; set; }
		public int TotalRepliesOnFeed { get; set; }
		public int TotalQuotesOnFeed { get; set; }
	}
}