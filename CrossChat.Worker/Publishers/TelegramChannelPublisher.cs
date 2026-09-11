using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types.ReplyMarkups;

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

		// 1. Формируем кнопку-ссылку, если она заполнена
		InlineKeyboardMarkup? inlineKeyboard = _service.BuildInlineButton(state.ButtonText, state.ButtonUrl);

		bool isAudio(string s) => s.StartsWith("data:audio", StringComparison.OrdinalIgnoreCase) || s.Contains("audio/");
		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
		bool isImage(string s) => !isAudio(s) && !isVideo(s);

		var audioItem = images?.FirstOrDefault(isAudio);
		var visualMedia = images?.Where(s => !isAudio(s)).ToList() ?? new List<string>();

		// === 1. ПЛАТНЫЙ ПОСТ (STARS) ===
		if (state.IsPaid && state.Price > 0)
		{
			if (!visualMedia.Any())
				throw new Exception("Платный пост в Telegram требует хотя бы одно фото или видео.");

			await _console.Log($"Отправка платного поста ({state.Price} ⭐) в Telegram...", settings.UserId, state.BotId);
			await _service.SendPaidMediaGroupAsync(settings.ChannelId, state.Price, visualMedia, caption, inlineKeyboard);

			if (audioItem != null)
			{
				await Task.Delay(500);
				await _service.SendVoiceAsync(settings.ChannelId, audioItem, "");
			}
		}
		// === 2. ВИЗУАЛЬНЫЕ МЕДИА ===
		else if (visualMedia.Any())
		{
			if (visualMedia.Count == 1)
			{
				if (isVideo(visualMedia[0]))
				{
					if (state.IsVideoNote)
					{
						await _console.Log("Отправка кружочка в Telegram...", settings.UserId, state.BotId);
						await _service.SendVideoNoteAsync(settings.ChannelId, visualMedia[0], caption);
						// К кружочку кнопку шлем следом
						if (inlineKeyboard != null)
						{
							await _service.SendMessage(settings.ChannelId, "👇", inlineKeyboard);
						}
					}
					else
					{
						await _console.Log("Отправка видео с кнопкой в Telegram...", settings.UserId, state.BotId);
						await _service.SendSingleVideoAsync(settings.ChannelId, visualMedia[0], caption, replyMarkup: inlineKeyboard);
					}
				}
				else
				{
					await _console.Log("Отправка фото с кнопкой в Telegram...", settings.UserId, state.BotId);
					await _service.SendSinglePhotoAsync(settings.ChannelId, visualMedia[0], caption, replyMarkup: inlineKeyboard);
				}
			}
			else
			{
				await _console.Log($"Отправка альбома из {visualMedia.Count} файлов в Telegram...", settings.UserId, state.BotId);
				await _service.SendMediaAlbumAsync(settings.ChannelId, visualMedia, caption, inlineKeyboard);
			}

			if (audioItem != null)
			{
				await Task.Delay(1000);
				await _service.SendVoiceAsync(settings.ChannelId, audioItem, "");
			}
		}
		// === 3. ТОЛЬКО ГОЛОСОВОЕ ===
		else if (audioItem != null)
		{
			await _console.Log("Отправка голосового сообщения с кнопкой в Telegram...", settings.UserId, state.BotId);
			await _service.SendVoiceAsync(settings.ChannelId, audioItem, caption, replyMarkup: inlineKeyboard);
		}
		// === 4. ТОЛЬКО ТЕКСТ ===
		else
		{
			await _console.Log("Отправка текста с кнопкой в Telegram...", settings.UserId, state.BotId);
			await _service.SendMessage(settings.ChannelId, caption, inlineKeyboard);
		}

		await _console.Log($"Пост успешно опубликован в канал {settings.ChannelUsername}.", settings.UserId, state.BotId);
	}
}