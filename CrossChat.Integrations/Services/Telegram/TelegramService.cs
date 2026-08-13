using CrossChat.Integrations.Interfaces;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CrossChat.Integrations.Services.Telegram
{
	public class TelegramService : ITelegramService
	{
		private readonly ITelegramBotClient _telegramBotClient;
		private readonly long ADMIN_ID;
		private const string TELEGRAM_ADMIN_ID = "TELEGRAM_ADMIN_ID";

		public TelegramService(ITelegramBotClient telegramBotClient, IConfiguration configuration)
		{
			_telegramBotClient = telegramBotClient;
			var adminId = configuration[TELEGRAM_ADMIN_ID] ?? Environment.GetEnvironmentVariable(TELEGRAM_ADMIN_ID);
			ADMIN_ID = long.Parse(adminId); ;
		}

		public Task<Message> SendMessage(long senderId, string text, ReplyMarkup replyMarkup)
		{
			return _telegramBotClient.SendMessage(senderId, text, replyMarkup: replyMarkup);
		}

		public Task<Message> SendMessage(long senderId, string text)
		{
			return _telegramBotClient.SendMessage(senderId, text);
		}

		public Task<Message> SendMessageToAdmin(string text, ReplyMarkup replyMarkup = null)
		{
			return SendMessage(text, ADMIN_ID, replyMarkup: replyMarkup);
		}

		public Task<Message> SendMessage(string text
			, long senderId
			, int? replayMsgId = null
			, ParseMode parseMode = ParseMode.Html
			, ReplyMarkup replyMarkup = null
			, CancellationToken cancellationToken = default)
		{
			if (text.Length > 4096)
			{
				text = text.Substring(0, 4000) + "\n...(обрезано)";
				parseMode = ParseMode.Html;
			}

			if (replayMsgId is null)
			{
				return _telegramBotClient.SendMessage(senderId, text, parseMode: parseMode, replyMarkup: replyMarkup, cancellationToken: cancellationToken);
			}

			return _telegramBotClient.SendMessage(senderId, text,
				replyParameters: new ReplyParameters { MessageId = replayMsgId.Value },
				parseMode: parseMode, replyMarkup: replyMarkup, cancellationToken: cancellationToken);
		}

		public async Task<Message> SendSinglePhotoAsync(long senderId, string base64Image, string caption = "", ParseMode parseMode = ParseMode.None, ReplyMarkup replyMarkup = null)
		{
			var imageBytes = Convert.FromBase64String(base64Image);

			// Проверка длины ДО отправки (1024 - лимит Telegram для caption)
			bool isCaptionTooLong = caption.Length > 1024;

			using (var stream = new MemoryStream(imageBytes))
			{
				if (isCaptionTooLong)
				{
					// Сценарий: Длинное описание
					// 1. Шлем фото пустным
					var photoMsg = await _telegramBotClient.SendPhoto(senderId, InputFile.FromStream(stream, "image.jpg"));

					// 2. Шлем текст отдельно
					await _telegramBotClient.SendMessage(senderId, caption, replyMarkup: replyMarkup, parseMode: parseMode);

					return photoMsg;
				}
				else
				{
					// Сценарий: Нормальное описание
					return await _telegramBotClient.SendPhoto(senderId, InputFile.FromStream(stream, "image.jpg"), caption, replyMarkup: replyMarkup, parseMode: parseMode);
				}
			}
		}

		public async Task<Message[]> SendPhotoAlbumAsync(long senderId, List<string> base64Images, string caption = "")
		{
			var media = new List<IAlbumInputMedia>();
			var streams = new List<MemoryStream>(); // храним ссылки на стримы
			Message[] messages = null;

			try
			{
				for (int i = 0; i < base64Images.Count; i++)
				{
					var imageBytes = Convert.FromBase64String(base64Images[i]);
					var stream = new MemoryStream(imageBytes); // без using!
					streams.Add(stream); // сохраняем ссылку

					var inputMedia = new InputMediaPhoto(InputFile.FromStream(stream, $"image_{i}.jpg"));

					if (i == 0 && !string.IsNullOrEmpty(caption))
					{
						inputMedia.Caption = caption;
						inputMedia.ParseMode = ParseMode.Html;
					}

					media.Add(inputMedia);
				}

				messages = await _telegramBotClient.SendMediaGroup(senderId, media);

				return messages;
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message);
				return null;
			}
			finally
			{
				// Освобождаем ресурсы после отправки
				foreach (var stream in streams)
				{
					stream?.Dispose();
				}
			}
		}

		public async Task<Message> SendPaidPhotosAsync(long senderId, IEnumerable<string> base64Images, int starCount, string caption = "", ParseMode parseMode = ParseMode.None)
		{
			if (base64Images == null || !base64Images.Any())
			{
				Console.WriteLine("Список изображений пуст.");
				return null;
			}

			// Список для хранения потоков, чтобы закрыть их после отправки
			var streams = new List<MemoryStream>();

			// Список медиа-объектов для Телеграма
			var paidMediaItems = new List<InputPaidMedia>();

			try
			{
				int index = 1;
				foreach (var base64 in base64Images)
				{
					// Конвертируем строку в байты
					var imageBytes = Convert.FromBase64String(base64);

					// Создаем поток
					var stream = new MemoryStream(imageBytes);

					// Добавляем поток в список очистки (чтобы он не потерялся и мы могли его закрыть)
					streams.Add(stream);

					// Добавляем фото в список медиа
					paidMediaItems.Add(new InputPaidMediaPhoto
					{
						Media = InputFile.FromStream(stream, $"paid_image_{index}.jpg")
					});

					index++;
				}

				// Отправляем весь пакет
				return await _telegramBotClient.SendPaidMedia(
					chatId: senderId,
					starCount: starCount,
					media: paidMediaItems,
					caption: caption,
					parseMode: parseMode
				);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка отправки платного альбома: {ex.Message}");
				throw;
			}
			finally
			{
				// ВАЖНО: Очищаем все потоки после отправки (или ошибки)
				foreach (var stream in streams)
				{
					stream.Dispose();
				}
			}
		}

		public async Task<string?> GetChannelAvatarBase64Async(long channelId)
		{
			try
			{
				// 1. Запрашиваем информацию о канале
				var chat = await _telegramBotClient.GetChat(channelId);

				if (chat.Photo != null && !string.IsNullOrEmpty(chat.Photo.BigFileId))
				{
					// 2. Получаем объект файла с путем FilePath
					var file = await _telegramBotClient.GetFile(chat.Photo.BigFileId);

					if (file.FilePath != null)
					{
						// 3. Используем метод библиотеки DownloadFile напрямую в MemoryStream!
						using var ms = new MemoryStream();
						await _telegramBotClient.DownloadFile(file.FilePath, ms);

						// 4. Переводим байты из памяти в готовую для HTML Base64-строку
						var base64String = Convert.ToBase64String(ms.ToArray());
						return $"data:image/jpeg;base64,{base64String}";
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram Channel] Не удалось скачать аватарку для канала {channelId}" + ex);
			}

			return null;
		}

		public async Task<string?> GetChannelAvatarBase64ByFileIdAsync(string fileId)
		{
			try
			{
				var file = await _telegramBotClient.GetFile(fileId);
				if (file.FilePath != null)
				{
					using var ms = new MemoryStream();
					await _telegramBotClient.DownloadFile(file.FilePath, ms);
					return $"data:image/jpeg;base64,{Convert.ToBase64String(ms.ToArray())}";
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram Webhook] Не удалось скачать фото по FileId {fileId} {ex}");
			}

			return null;
		}

		public async Task SetWebhookAsync(string token, string webhookUrl)
		{
			var bot = new TelegramBotClient(token);
			await bot.SetWebhook(webhookUrl);
		}

		public async Task DeleteWebhookAsync(string token)
		{
			var bot = new TelegramBotClient(token);
			await bot.DeleteWebhook();
		}

		public async Task SendMessageAsync(string token, long chatId, string text)
		{
			var bot = new TelegramBotClient(token);
			await bot.SendMessage(chatId, text);
		}

	}
}
