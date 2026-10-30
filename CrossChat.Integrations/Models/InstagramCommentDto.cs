using System.Text.Json.Serialization;

namespace CrossChat.Integrations.Models
{
	/// <summary>
	/// Древовидная модель комментария Instagram (подходит и для корневых комментариев, и для ответов)
	/// </summary>
	public class InstagramCommentItemDto
	{
		[JsonPropertyName("id")]
		public string Id { get; set; } = string.Empty;

		[JsonPropertyName("text")]
		public string Text { get; set; } = string.Empty;

		[JsonPropertyName("username")]
		public string Username { get; set; } = string.Empty;

		[JsonPropertyName("timestamp")]
		public DateTime Timestamp { get; set; } = DateTime.UtcNow;

		[JsonPropertyName("likeCount")]
		public int LikeCount { get; set; } = 0;

		/// <summary>
		/// Список вложенных ответов (Replies) к этому комментарию
		/// </summary>
		[JsonPropertyName("replies")]
		public List<InstagramCommentItemDto> Replies { get; set; } = new();
	}
}