using CrossChat.Data.Emuns;

namespace CrossChat.Integrations.Models.Posting
{
	public class PostMediaItem
	{
		public int Id { get; set; }
		public MediaType MediaType { get; set; }
		public string GoogleDriveFileId { get; set; } = string.Empty;
		public string? ThumbnailDriveFileId { get; set; }
		public string FileName { get; set; } = string.Empty;
		public string MimeType { get; set; } = string.Empty;
		public long FileSizeBytes { get; set; }
		public int SortOrder { get; set; }
	}
}
