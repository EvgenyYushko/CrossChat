namespace CrossChat.Integrations.Models
{
	public class FacebookDailyStoryDto
	{
		public string PageId { get; set; } = string.Empty;
		public string PageAccessToken { get; set; } = string.Empty;
		public string PageName { get; set; } = string.Empty;
		public string? UsedMediaIdsJson { get; set; }
		public bool IsStoryOverlayTextEnabled { get; set; }
		public string? StoryOverlayText { get; set; }
	}
}
