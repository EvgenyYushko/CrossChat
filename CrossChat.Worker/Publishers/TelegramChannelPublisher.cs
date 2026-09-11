using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class TelegramChannelPublisher : ISocialPublisher
{
	public NetworkType Network => NetworkType.TelegramChannel;

	private readonly AppDbContext _db;
	private readonly ITelegramService _service;
	private readonly ITelegramChannelConsole _console;

	public TelegramChannelPublisher(AppDbContext db, ITelegramService service, ITelegramChannelConsole console)
	{
		_db = db;
		_service = service;
		_console = console;
	}

	public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
	{
		var settings = await _db.TelegramChannelSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
		if (settings == null)
			throw new Exception($"Telegram канал (BotId: {state.BotId}) не найден.");

		await _console.Log($"Начало отправки поста в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);

		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");

		// === 1. СЦЕНАРИЙ: ПЛАТНЫЙ ПОСТ (TELEGRAM STARS) ===
		if (state.IsPaid && state.Price > 0)
		{
			if (images == null || !images.Any())
			{
				throw new Exception("Telegram не поддерживает платные посты без медиафайлов. Прикрепите хотя бы одно фото или видео.");
			}

			await _console.Log($"Отправка платного поста ({state.Price} ⭐) из {images.Count} медиафайлов в Telegram...", settings.UserId, state.BotId);
			await _service.SendPaidMediaGroupAsync(settings.ChannelId, state.Price, images, caption);
		}
		// === 2. СЦЕНАРИЙ: ОБЫЧНЫЙ БЕСПЛАТНЫЙ МЕДИА-ПОСТ ===
		else if (images != null && images.Any())
		{
			if (images.Count == 1)
			{
				if (isVideo(images[0]))
				{
					await _console.Log("Отправка видео в Telegram канал...", settings.UserId, state.BotId);
					await _service.SendSingleVideoAsync(settings.ChannelId, images[0], caption);
				}
				else
				{
					await _console.Log("Отправка фото в Telegram канал...", settings.UserId, state.BotId);
					await _service.SendSinglePhotoAsync(settings.ChannelId, images[0], caption);
				}
			}
			else
			{
				await _console.Log($"Отправка альбома из {images.Count} медиафайлов в Telegram канал...", settings.UserId, state.BotId);
				var result = await _service.SendMediaAlbumAsync(settings.ChannelId, images, caption);
				if (result == null || result.Length == 0)
				{
					throw new Exception("Не удалось отправить медиа-альбом в Telegram канал.");
				}
			}
		}
		// === 3. СЦЕНАРИЙ: ТОЛЬКО ТЕКСТ ===
		else
		{
			await _console.Log("Отправка текстового сообщения в Telegram канал...", settings.UserId, state.BotId);
			await _service.SendMessage(settings.ChannelId, caption);
		}

		await _console.Log($"Пост успешно опубликован в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);
	}
}