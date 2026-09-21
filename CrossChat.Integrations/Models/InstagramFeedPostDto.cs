namespace CrossChat.Integrations.Models
{
	public class InstagramFeedPostDto
	{
		public string Id { get; set; } = string.Empty;
		public string? Caption { get; set; }
		public string MediaType { get; set; } = "IMAGE"; // IMAGE, VIDEO, CAROUSEL_ALBUM
		public string? MediaUrl { get; set; }
		public string? ThumbnailUrl { get; set; } // Обложка для видео
		public string? Permalink { get; set; }
		public int LikeCount { get; set; }
		public int CommentsCount { get; set; }
		public DateTime Timestamp { get; set; }
	}

	public class InstagramFeedPageDto
	{
		public List<InstagramFeedPostDto> Posts { get; set; } = new();
		public string? AfterCursor { get; set; }
		public string? BeforeCursor { get; set; }
		public bool HasNext => !string.IsNullOrEmpty(AfterCursor);
		public bool HasPrevious => !string.IsNullOrEmpty(BeforeCursor);
	}

	public class InstagramPostInsightsDto
	{
		public int Reach { get; set; }         // Охват (уникальные аккаунты)
		public int Impressions { get; set; }   // Показы (всего просмотров)
		public int Plays { get; set; }         // Просмотры Reels
		public int Saved { get; set; }         // Сохранения в закладки
		public int Shares { get; set; }        // Репосты в Директ
		public int TotalInteractions { get; set; } // Суммарные взаимодействия
	}
}
