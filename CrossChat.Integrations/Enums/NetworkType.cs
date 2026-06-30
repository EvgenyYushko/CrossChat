namespace CrossChat.Integrations.Enums
{
	/// <summary>
	/// Перечисление поддерживаемых социальных сетей и платформ.
	/// </summary>
	public enum NetworkType
	{
		/// <summary>
		/// Мета-значение, обозначающее выбор "Во все сети" или отсутствие фильтрации.
		/// Не является конкретной платформой.
		/// </summary>
		All,

		/// <summary>
		/// Платформа Instagram.
		/// </summary>
		Instagram,

		/// <summary>
		/// Платформа Facebook.
		/// </summary>
		Facebook,

		/// <summary>
		/// Платформа BlueSky.
		/// </summary>
		BlueSky,

		/// <summary>
		/// Основной (публичный) канал Telegram.
		/// </summary>
		TelegramPublic,

		/// <summary>
		/// Приватный (закрытый) канал Telegram.
		/// </summary>
		TelegramPrivate,

		/// <summary>
		/// X (Twiter)
		/// </summary>
		X,

		/// <summary>
		/// Threads
		/// </summary>
		Threads
	}
}
