namespace CrossChat.Integrations.Models
{
	public class InstagramAccountInsightsDto
	{
		public int Reach { get; set; }           // 👥 Охват (уникальные пользователи за 28 дней)
		public int ProfileViews { get; set; }    // 👤 Просмотры профиля
		public int WebsiteClicks { get; set; }   // 🔗 Клики по ссылке в шапке (переходы в Telegram)
		public int AccountsEngaged { get; set; } // 💬 Вовлеченные аккаунты (поставившие реакции/комменты)
	}
}
