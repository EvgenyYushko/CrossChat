namespace CrossChat.Integrations.Enums
{
	/// <summary>
	/// Состояние пользователя в диалоге с ботом (Машина состояний / FSM).
	/// Используется для определения контекста входящих текстовых сообщений или файлов.
	/// </summary>
	public enum UserState
	{
		/// <summary>
		/// Нет активного действия. Бот ожидает команды из меню.
		/// </summary>
		None,

		/// <summary>
		/// Бот ожидает отправку фотографии (или альбома) для создания нового поста.
		/// </summary>
		WaitingForPhoto,

		/// <summary>
		/// Бот ожидает ввод текста для изменения описания (Caption) существующего поста.
		/// </summary>
		WaitingForEditCaption
	}
}
