namespace CrossChat.Integrations.Enums
{
	/// <summary>
	/// Текущий статус публикации поста в конкретной социальной сети.
	/// </summary>
	public enum SocialStatus
	{
		/// <summary>
		/// Публикация в эту сеть не запланирована (или была отменена/исключена).
		/// </summary>
		None,

		/// <summary>
		/// Пост находится в очереди и ожидает публикации.
		/// </summary>
		Pending,

		/// <summary>
		/// Пост успешно опубликован.
		/// </summary>
		Published,

		/// <summary>
		/// При публикации произошла ошибка. Требуется внимание или повторная попытка.
		/// </summary>
		Error
	}
}
