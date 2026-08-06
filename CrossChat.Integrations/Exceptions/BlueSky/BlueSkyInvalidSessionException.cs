using System.Net;

namespace CrossChat.Integrations.Exceptions.BlueSky
{
	public class BlueSkyInvalidSessionException : Exception
	{
		public BlueSkyInvalidSessionException() : base("❌ Сессия не активна или PDS URL не определен. Выполните Login/UpdateSession.")
		{
		}

		public BlueSkyInvalidSessionException(string text) : base(text + "❌ Сессия не активна или PDS URL не определен. Выполните Login/UpdateSession.")
		{
		}
	}

	public class BlueSkyConnectionSessionException : Exception
	{
		public BlueSkyConnectionSessionException() : base("❌ Невозможно обновить сессию: Refresh Token не задан.")
		{
		}

		public BlueSkyConnectionSessionException(string text) : base(text)
		{
		}
	}

	public class BlueSkyCreatePostException : Exception
	{
		public BlueSkyCreatePostException() : base("❌ Сессия не активна или PDS URL не определен. Выполните Login/UpdateSession.")
		{
		}

		public BlueSkyCreatePostException(HttpStatusCode statusCode, string errorContent) : base($"❌ Ошибка при создании поста: {statusCode} - {errorContent}")
		{
		}
	}
}
