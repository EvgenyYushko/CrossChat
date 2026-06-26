namespace CrossChat.Integrations.Enums
{
	/// <summary>
	/// Фильтр для выборки постов в интерфейсе бота (меню "Просмотр очереди").
	/// Позволяет пользователю видеть только нужные типы постов.
	/// </summary>
	public enum AccessFilter
	{
		/// <summary>
		/// Показать все посты без фильтрации.
		/// </summary>
		All,

		/// <summary>
		/// Показать только публичные посты.
		/// </summary>
		Public,

		/// <summary>
		/// Показать только приватные посты.
		/// </summary>
		Private
	}
}
