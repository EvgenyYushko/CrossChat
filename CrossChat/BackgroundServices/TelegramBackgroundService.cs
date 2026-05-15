using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.BackgroundServices
{
	public class TelegramBackgroundService : BackgroundService
	{
		private readonly ITelegramUserBotService _telegramUserBotService;
		private AppDbContext _db;
		private readonly IServiceScopeFactory _serviceScopeFactory;

		public TelegramBackgroundService(ITelegramUserBotService telegramUserBotService
			, IServiceScopeFactory serviceScopeFactory
			)
		{
			_telegramUserBotService = telegramUserBotService;
			_serviceScopeFactory = serviceScopeFactory;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			using (var scope = _serviceScopeFactory.CreateScope())
			{
				// Достаем наш новый сервис публикации
				_db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

				var bots = await _db.TelegramUsersBotSettings.Where(b => b.IsActive).ToListAsync();

				foreach (var bot in bots)
				{
					var dto = new UserBotDto()
					{
						AuthKey = bot.AuthKey,
						DcId = bot.DcId,
						Id = bot.Id,
						ProxyHost = bot.ProxyHost,
						ProxyPass = bot.ProxyPass,
						ProxyPort = bot.ProxyPort,
						ProxyUser = bot.ProxyUser,
						TgUserId = bot.UserId,
						SessionBytes = bot.SessionData
					};

					var client = await _telegramUserBotService.CreateAndConnectAsync(dto);

					// Навешиваем событие прослушивания
					//client.OnUpdates += async (update) =>
					//{
					//	if (update is TL.UpdateShortMessage msg)
					//	{
					//		// Кидаем в очередь на ответ через ИИ
					//	}
					//};
				}

			}

			await Task.Delay(-1, stoppingToken);
		}
	}
}
