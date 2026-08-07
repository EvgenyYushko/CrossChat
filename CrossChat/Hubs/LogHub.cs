using System.Security.Claims;
using CrossChat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Hubs
{
	[Authorize] // Только авторизованные пользователи могут слушать логи
	public class LogHub : Hub
	{
		private readonly AppDbContext _db;

		public LogHub(AppDbContext db)
		{
			_db = db;
		}

		public override async Task OnConnectedAsync()
		{
			var httpContext = Context.GetHttpContext();
			var rawProvider = httpContext?.Request.Query["provider"].ToString();

			// Нормализуем строку
			var provider = rawProvider?.ToLower();
			var botIdStr = httpContext?.Request.Query["botId"].ToString();

			// 1. Получаем ID текущего пользователя из куки
			var userIdStr = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

			if (int.TryParse(userIdStr, out var userId) &&
				int.TryParse(botIdStr, out var botId) &&
				!string.IsNullOrEmpty(provider))
			{
				// 2. ПРОВЕРКА ВЛАДЕНИЯ (Security Check)
				bool isOwner = false;

				// Все строки сравнения теперь строго в НИЖНЕМ РЕГИСТРЕ!
				if (provider == "instagram")
					isOwner = await _db.InstagramSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "telegramchannel")
					isOwner = await _db.TelegramChannelSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "telegram")
					isOwner = await _db.TelegramUsersBotSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "threads")
					isOwner = await _db.ThreadsSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "bluesky")
					isOwner = await _db.BlueSkySettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "facebook")
					isOwner = await _db.FacebookSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);
				else if (provider == "x")
					isOwner = await _db.XSettings.AnyAsync(s => s.Id == botId && s.UserId == userId);

				if (isOwner)
				{
					var groupName = $"bot_room_{provider}_{botId}";
					await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
				}
				else
				{
					// Если не владелец — обрываем связь
					Context.Abort();
				}
			}

			await base.OnConnectedAsync();
		}

		public override async Task OnDisconnectedAsync(Exception? exception)
		{
			var httpContext = Context.GetHttpContext();
			var provider = httpContext?.Request.Query["provider"].ToString();
			var botId = httpContext?.Request.Query["botId"].ToString();

			if (!string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(botId))
			{
				var groupName = $"bot_room_{provider.ToLower()}_{botId}";
				await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
			}
			await base.OnDisconnectedAsync(exception);
		}
	}
}
