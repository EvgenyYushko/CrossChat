namespace CrossChat.Integrations.Models
{
	public class UserBotDto
	{
		public int Id { get; set; } // Внутренний ID для имени файла сессии
									// Параметры для инъекции (если сессии еще нет)
		public int DcId { get; set; }
		public string AuthKey { get; set; } = string.Empty;
		public long TgUserId { get; set; }

		// Прокси
		public string? ProxyHost { get; set; }
		public int? ProxyPort { get; set; }
		public string? ProxyUser { get; set; }
		public string? ProxyPass { get; set; }
		public string? TgUserName { get; set; }

		// Байты сессии из БД (null при первом запуске)
		public byte[]? SessionBytes { get; set; }
	}
}
