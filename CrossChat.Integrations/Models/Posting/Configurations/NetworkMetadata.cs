using CrossChat.Integrations.Enums;

namespace CrossChat.Integrations.Models.Posting.Configurations
{
	// --- 1. НАСТРОЙКИ СЕТЕЙ (ЕДИНАЯ ТОЧКА КОНФИГУРАЦИИ) ---
	// Чтобы добавить соцсеть, добавьте её в Enum и сюда.
	public static class NetworkMetadata
	{
		public static readonly Dictionary<NetworkType, (string Name, string Icon)> Info = new()
		{
			{ NetworkType.Instagram, ("Instagram", "📸") },
			{ NetworkType.Facebook, ("Facebook", "👥") } ,
			{ NetworkType.BlueSky,   ("BlueSky", "💠") },
			{ NetworkType.X, ("X", "✗") },
			{ NetworkType.TelegramPublic, ("Telegram Public", "📱") },
			{ NetworkType.TelegramPrivate, ("Telegram Private", "💋") },
		};

		// Список поддерживаемых сетей (исключая All)
		public static IEnumerable<NetworkType> Supported => Info.Keys;

		// Куда постить, если нажали "Во все Публичные"
		public static readonly List<NetworkType> PublicSet = new()
		{
			NetworkType.Instagram,
			NetworkType.Facebook,
			NetworkType.BlueSky,
			NetworkType.X,
			NetworkType.TelegramPublic,
		};

		// Куда постить, если нажали "Во все Приватные"
		public static readonly List<NetworkType> PrivateSet = new()
		{
			NetworkType.TelegramPrivate // Пока только телеграм
			// В будущем добавите сюда другие приватные каналы
		};
	}
}
