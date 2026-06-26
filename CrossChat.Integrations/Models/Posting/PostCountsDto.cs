namespace CrossChat.Integrations.Models.Posting
{
	public class PostCountsDto
	{
		public int Pending { get; set; }   // Ожидают публикации
		public int Errors { get; set; }    // Требуют внимания (ошибки)
		public int Published { get; set; } // Полностью опубликованы
		public int Total { get; set; }     // Всего постов
	}
}
