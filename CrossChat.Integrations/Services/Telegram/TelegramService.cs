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

		/// <summary>
		/// Публикация платного контента (Telegram Stars) — от 1 до 10 фото и/или видео, скрытых блюром
		/// </summary>
		public async Task<Message> SendPaidMediaGroupAsync(long senderId, int starCount, List<string> base64MediaList, string caption = "")
		{
			if (base64MediaList == null || !base64MediaList.Any())
				throw new ArgumentException("Для платного поста в Telegram требуется хотя бы одно фото или видео.");

			// Лимит Telegram: от 1 до 2500 звезд
			starCount = Math.Clamp(starCount, 1, 2500);

			var streams = new List<MemoryStream>();
			var paidMediaItems = new List<InputPaidMedia>();

			try
			{
				bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
				string? mediaCaption = isCaptionTooLong ? null : caption;

				for (int i = 0; i < Math.Min(base64MediaList.Count, 10); i++)
				{
					var item = base64MediaList[i];
					bool isVideo = item.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || item.Contains("video/");
					string cleanBase64 = item.Contains(",") ? item.Split(',')[1] : item;

					var bytes = Convert.FromBase64String(cleanBase64);
					var stream = new MemoryStream(bytes);
					streams.Add(stream);

					if (isVideo)
					{
						paidMediaItems.Add(new InputPaidMediaVideo
						{
							Media = InputFile.FromStream(stream, $"paid_video_{i}.mp4"),
							SupportsStreaming = true
						});
					}
					else
					{
						paidMediaItems.Add(new InputPaidMediaPhoto
						{
							Media = InputFile.FromStream(stream, $"paid_image_{i}.jpg")
						});
					}
				}

				// Отправляем платный альбом/медиа
				var message = await _telegramBotClient.SendPaidMedia(
					chatId: senderId,
					starCount: starCount,
					media: paidMediaItems,
					caption: mediaCaption,
					parseMode: ParseMode.Html
				);

				// Если описание было длиннее 1024 символов — отправляем следом
				if (isCaptionTooLong)
				{
					await _telegramBotClient.SendMessage(senderId, caption, parseMode: ParseMode.Html);
				}

				return message;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram] Ошибка отправки платного медиа: {ex.Message}");
				throw;
			}
			finally
			{
				foreach (var stream in streams)
				{
					stream?.Dispose();
				}
			}
		}

		public async Task<Message> SendSinglePhotoAsync(long senderId, string base64Image, string caption = "", ParseMode parseMode = ParseMode.None, ReplyMarkup replyMarkup = null)
		{
			// Очищаем префикс Base64, если он есть
			string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;
			var imageBytes = Convert.FromBase64String(cleanBase64);

			bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;

			using (var stream = new MemoryStream(imageBytes))
			{
				if (isCaptionTooLong)
				{
					// Если описание длинное: шлем фото без текста, а текст — отдельным сообщением
					var photoMsg = await _telegramBotClient.SendPhoto(senderId, InputFile.FromStream(stream, "image.jpg"));
					await _telegramBotClient.SendMessage(senderId, caption, replyMarkup: replyMarkup, parseMode: parseMode);
					return photoMsg;
				}
				else
				{
					return await _telegramBotClient.SendPhoto(senderId, InputFile.FromStream(stream, "image.jpg"),
						caption: string.IsNullOrEmpty(caption) ? null : caption,
						replyMarkup: replyMarkup,
						parseMode: parseMode);
				}
			}
		}

		public async Task<Message> SendSingleVideoAsync(long senderId, string base64Video, string caption = "", ParseMode parseMode = ParseMode.None, ReplyMarkup replyMarkup = null)
		{
			string cleanBase64 = base64Video.Contains(",") ? base64Video.Split(',')[1] : base64Video;
			var videoBytes = Convert.FromBase64String(cleanBase64);

			bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;

			using (var stream = new MemoryStream(videoBytes))
			{
				if (isCaptionTooLong)
				{
					var videoMsg = await _telegramBotClient.SendVideo(
						chatId: senderId,
						video: InputFile.FromStream(stream, "video.mp4"),
						supportsStreaming: true);

					await _telegramBotClient.SendMessage(
						chatId: senderId,
						text: caption,
						replyMarkup: replyMarkup,
						parseMode: parseMode);

					return videoMsg;
				}
				else
				{
					return await _telegramBotClient.SendVideo(
						chatId: senderId,
						video: InputFile.FromStream(stream, "video.mp4"),
						caption: string.IsNullOrEmpty(caption) ? null : caption,
						replyMarkup: replyMarkup,
						parseMode: parseMode,
						supportsStreaming: true);
				}
			}
		}

		/// <summary>
		/// Универсальная публикация альбома: поддерживает и фото, и видео, и их смесь (до 10 файлов)
		/// </summary>
		public async Task<Message[]> SendMediaAlbumAsync(long senderId, List<string> base64MediaList, string caption = "")
		{
			if (base64MediaList == null || !base64MediaList.Any()) return null;

			var media = new List<IAlbumInputMedia>();
			var streams = new List<MemoryStream>();

			try
			{
				bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
				string? albumCaption = isCaptionTooLong ? null : caption;

				for (int i = 0; i < Math.Min(base64MediaList.Count, 10); i++)
				{
					var item = base64MediaList[i];
					bool isVideo = item.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || item.Contains("video/");
					string cleanBase64 = item.Contains(",") ? item.Split(',')[1] : item;

					var bytes = Convert.FromBase64String(cleanBase64);
					var stream = new MemoryStream(bytes);
					streams.Add(stream);

					// Задаем описание только для первого элемента альбома
					string? itemCaption = (i == 0 && !string.IsNullOrEmpty(albumCaption)) ? albumCaption : null;
					ParseMode itemParseMode = itemCaption != null ? ParseMode.Html : ParseMode.None;

					IAlbumInputMedia inputMedia;

					if (isVideo)
					{
						inputMedia = new InputMediaVideo(InputFile.FromStream(stream, $"video_{i}.mp4"))
						{
							SupportsStreaming = true,
							Caption = itemCaption,
							ParseMode = itemParseMode
						};
					}
					else
					{
						inputMedia = new InputMediaPhoto(InputFile.FromStream(stream, $"image_{i}.jpg"))
						{
							Caption = itemCaption,
							ParseMode = itemParseMode
						};
					}

					media.Add(inputMedia);
				}

				var messages = await _telegramBotClient.SendMediaGroup(senderId, media);

				// Если текст не поместился в лимит подписи 1024 — отправляем следом отдельным сообщением
				if (isCaptionTooLong)
				{
					await _telegramBotClient.SendMessage(senderId, caption, parseMode: ParseMode.Html);
				}

				return messages;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram] Ошибка при отправке медиа-альбома: {ex.Message}");
				return null;
			}
			finally
			{
				foreach (var stream in streams)
				{
					stream?.Dispose();
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
