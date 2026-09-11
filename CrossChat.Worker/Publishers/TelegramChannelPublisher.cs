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

		// Определяем типы медиафайлов
		bool isAudio(string s) => s.StartsWith("data:audio", StringComparison.OrdinalIgnoreCase) || s.Contains("audio/");
		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
		bool isImage(string s) => !isAudio(s) && !isVideo(s);

		var audioItem = images?.FirstOrDefault(isAudio);
		var visualMedia = images?.Where(s => !isAudio(s)).ToList() ?? new List<string>();

		// === 1. СЦЕНАРИЙ: ПЛАТНЫЙ ПОСТ (TELEGRAM STARS) ===
		if (state.IsPaid && state.Price > 0)
		{
			if (!visualMedia.Any())
			{
				throw new Exception("Платный пост в Telegram требует хотя бы одно фото или видео.");
			}

			await _console.Log($"Отправка платного поста ({state.Price} ⭐) в Telegram...", settings.UserId, state.BotId);
			await _service.SendPaidMediaGroupAsync(settings.ChannelId, state.Price, visualMedia, caption);

			// Если к платному посту прикрепили еще и голосовое — шлем его следом
			if (audioItem != null)
			{
				await Task.Delay(500);
				await _console.Log("Отправка голосового сообщения к платному посту...", settings.UserId, state.BotId);
				await _service.SendVoiceAsync(settings.ChannelId, audioItem, "");
			}
		}
		// === 2. СЦЕНАРИЙ: ВИЗУАЛЬНЫЕ МЕДИА (ФОТО / ВИДЕО) ===
		else if (visualMedia.Any())
		{
			if (visualMedia.Count == 1)
			{
				if (isVideo(visualMedia[0]))
				{
					if (state.IsVideoNote)
					{
						await _console.Log("Отправка видео как кружочек (Video Note) в Telegram канал...", settings.UserId, state.BotId);
						await _service.SendVideoNoteAsync(settings.ChannelId, visualMedia[0], caption);
					}
					else
					{
						await _console.Log("Отправка обычного видео в Telegram канал...", settings.UserId, state.BotId);
						await _service.SendSingleVideoAsync(settings.ChannelId, visualMedia[0], caption);
					}
				}
				else
				{
					await _console.Log("Отправка фото в Telegram канал...", settings.UserId, state.BotId);
					await _service.SendSinglePhotoAsync(settings.ChannelId, visualMedia[0], caption);
				}
			}
			else
			{
				await _console.Log($"Отправка альбома из {visualMedia.Count} медиафайлов в Telegram...", settings.UserId, state.BotId);
				await _service.SendMediaAlbumAsync(settings.ChannelId, visualMedia, caption);
			}

			// БЛОГЕРСКИЙ СТИЛЬ: если к фото/видео прикрепили аудио — отправляем войс вторым сообщением следом!
			if (audioItem != null)
			{
				await Task.Delay(1000);
				await _console.Log("Отправка голосового сообщения следом за постом...", settings.UserId, state.BotId);
				await _service.SendVoiceAsync(settings.ChannelId, audioItem, "");
			}
		}
		// === 3. СЦЕНАРИЙ: ТОЛЬКО ГОЛОСОВОЕ СООБЩЕНИЕ ===
		else if (audioItem != null)
		{
			await _console.Log("Отправка голосового сообщения (Voice) в Telegram канал...", settings.UserId, state.BotId);
			await _service.SendVoiceAsync(settings.ChannelId, audioItem, caption);
		}
		// === 4. СЦЕНАРИЙ: ТОЛЬКО ТЕКСТ ===
		else
		{
			await _console.Log("Отправка текстового сообщения в Telegram канал...", settings.UserId, state.BotId);
			await _service.SendMessage(settings.ChannelId, caption);
		}

		await _console.Log($"Пост успешно опубликован в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);
	}
}