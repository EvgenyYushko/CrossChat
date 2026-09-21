namespace CrossChat.Integrations.Models
{
	public class InstagramDailyStoryDto
	{
		public string AccessToken { get; set; } = string.Empty;
		public string? Username { get; set; }
		public string? UsedMediaIdsJson { get; set; }
		public bool IsStoryOverlayTextEnabled { get; set; }
		public string? StoryOverlayText { get; set; }
	}

	public class DailyStoryResult
	{
		public bool Success { get; set; }
		public string? StoryId { get; set; }
		public string? NewUsedMediaIdsJson { get; set; }
	}
}
