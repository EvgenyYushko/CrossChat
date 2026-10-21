using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
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
			return _telegramBotClient.SendMessage(senderId, text, parseMode: ParseMode.Html, replyMarkup: replyMarkup);
		}

		public Task<Message> SendMessage(long senderId, string text)
		{
			return _telegramBotClient.SendMessage(senderId, text, parseMode: ParseMode.Html);
		}

		public Task<Message> SendMessageToAdmin(string text, ReplyMarkup replyMarkup = null)
		{
			return SendMessage(text, ADMIN_ID, replyMarkup: replyMarkup, parseMode: ParseMode.Html);
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
		public async Task<Message> SendPaidMediaGroupAsync(long senderId, int starCount, List<string> base64MediaList, string caption = "", ReplyMarkup replyMarkup = null)
		{
			if (base64MediaList == null || !base64MediaList.Any())
				throw new ArgumentException("Для платного поста в Telegram требуется хотя бы одно фото или видео.");

			starCount = Math.Clamp(starCount, 1, 2500);

			var streams = new List<Stream>();
			var tempThumbFiles = new List<string>();
			var paidMediaItems = new List<InputPaidMedia>();

			try
			{
				bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
				string? mediaCaption = isCaptionTooLong ? null : caption;

				for (int i = 0; i < Math.Min(base64MediaList.Count, 10); i++)
				{
					var item = base64MediaList[i];
					bool isVideo = item.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
								   item.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
								   item.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) ||
								   item.Contains("video/");

					var stream = GetMediaStream(item, out _);
					streams.Add(stream);

					if (isVideo)
					{
						int width = 0;
						int height = 0;
						int duration = 0;
						InputFile? coverFile = null;
						InputFile? thumbFile = null;

						bool fileExists = File.Exists(item);
						Console.WriteLine($"[Telegram Stars Debug] Элемент #{i}: это видео. Путь: '{item}', На диске существует: {fileExists}");

						if (fileExists)
						{
							// 1. Извлекаем пропорции видео через FFmpeg
							var resInfo = await VideoService.GetVideoResolutionAsync(item);
							if (resInfo != null)
							{
								width = resInfo.Width;
								height = resInfo.Height;
								duration = resInfo.DurationSeconds;
								Console.WriteLine($"[Telegram Stars Debug] ✅ Разрешение извлечено: {width}x{height}, длительность: {duration}с");
							}
							else
							{
								Console.WriteLine("[Telegram Stars Debug] ⚠️ Не удалось извлечь разрешение через FFmpeg!");
							}

							// 2. ГЕНЕРИРУЕМ ОБЛОЖКУ (ИМЕННО ОНА ЗАДАЕТ ФОРМУ ПЕЙВОЛЛА 9:16!)
							string? thumbPath = await VideoService.GenerateVideoThumbnailAsync(item);
							if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
							{
								tempThumbFiles.Add(thumbPath);
								byte[] thumbBytes = await File.ReadAllBytesAsync(thumbPath);
								Console.WriteLine($"[Telegram Stars Debug] ✅ Обложка создана ({thumbBytes.Length} байт): {thumbPath}");

								// Создаем ДВА независимых потока для Cover и Thumbnail (защита от ошибки Content-Length!)
								var coverStream = new MemoryStream(thumbBytes);
								var thumbStream = new MemoryStream(thumbBytes);
								streams.Add(coverStream);
								streams.Add(thumbStream);

								coverFile = InputFile.FromStream(coverStream, $"cover_{i}.jpg");
								thumbFile = InputFile.FromStream(thumbStream, $"thumb_{i}.jpg");
							}
							else
							{
								Console.WriteLine("[Telegram Stars Debug] ⚠️ Не удалось сгенерировать файл обложки thumbPath!");
							}
						}

						paidMediaItems.Add(new InputPaidMediaVideo
						{
							Media = InputFile.FromStream(stream, $"paid_video_{i}.mp4"),
							Cover = coverFile,         // <-- ПЕРЕДАЕМ ОБЛОЖКУ ДЛЯ ЗАБЛЮРЕННОГО ПЕЙВОЛЛА!
							Thumbnail = thumbFile,     // <-- ПРЕВЬЮ ДЛЯ КЛИЕНТА
							Width = width,             // 1080
							Height = height,           // 1920
							Duration = duration,
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

				Console.WriteLine($"[Telegram Stars Debug] 🚀 Отправка SendPaidMedia на {senderId} (элементов: {paidMediaItems.Count})...");

				var message = await _telegramBotClient.SendPaidMedia(
					chatId: senderId,
					starCount: starCount,
					media: paidMediaItems,
					caption: mediaCaption,
					parseMode: ParseMode.Html,
					replyMarkup: isCaptionTooLong ? null : replyMarkup
				);

				Console.WriteLine($"[Telegram Stars Debug] 🎉 Успешно отправлено! MessageId: {message.Id}");

				if (isCaptionTooLong)
				{
					await _telegramBotClient.SendMessage(senderId, caption, parseMode: ParseMode.Html, replyMarkup: replyMarkup);
				}

				return message;
			}
			finally
			{
				foreach (var s in streams)
				{
					s?.Dispose();
				}

				foreach (var thumb in tempThumbFiles)
				{
					try { if (File.Exists(thumb)) File.Delete(thumb); } catch { }
				}
			}
		}

		public async Task<Message> SendSinglePhotoAsync(long senderId, string base64Image, string caption = "", ParseMode parseMode = ParseMode.Html, ReplyMarkup replyMarkup = null)
		{
			string cleanBase64 = base64Image.Contains(",") ? base64Image.Split(',')[1] : base64Image;
			var imageBytes = Convert.FromBase64String(cleanBase64);

			bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;

			using (var stream = new MemoryStream(imageBytes))
			{
				if (isCaptionTooLong)
				{
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

		public async Task<Message> SendSingleVideoAsync(long senderId, string base64Video, string caption = "", ParseMode parseMode = ParseMode.Html, ReplyMarkup replyMarkup = null)
		{
			bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
			using var stream = GetMediaStream(base64Video, out _);

			// =========================================================================
			// ИЗВЛЕКАЕМ ТОЧНЫЕ РАЗМЕРЫ (WIDTH / HEIGHT / DURATION)
			// Это гарантирует идеальный вертикальный прямоугольник вместо квадрата!
			// =========================================================================
			int? width = null;
			int? height = null;
			int? duration = null;

			if (File.Exists(base64Video))
			{
				var resInfo = await VideoService.GetVideoResolutionAsync(base64Video);
				if (resInfo != null)
				{
					width = resInfo.Width;
					height = resInfo.Height;
					duration = resInfo.DurationSeconds;
				}
			}

			if (isCaptionTooLong)
			{
				var videoMsg = await _telegramBotClient.SendVideo(
					chatId: senderId,
					video: InputFile.FromStream(stream, "video.mp4"),
					duration: duration,
					width: width,
					height: height,
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
					duration: duration,
					width: width,
					height: height,
					caption: string.IsNullOrEmpty(caption) ? null : caption,
					replyMarkup: replyMarkup,
					parseMode: parseMode,
					supportsStreaming: true);
			}
		}

		/// <summary>
		/// Универсальная публикация медиа-альбома в Telegram (до 10 фото и/или видео)
		/// Поддерживает как файлы с диска (0 МБ RAM), так и Base64, сохраняя оригинальные пропорции (Width/Height)
		/// </summary>
		public async Task<Message[]> SendMediaAlbumAsync(long senderId, List<string> base64MediaList, string caption = "", ReplyMarkup replyMarkup = null)
		{
			if (base64MediaList == null || !base64MediaList.Any()) return null;

			var media = new List<IAlbumInputMedia>();
			var streams = new List<Stream>(); // Используем Stream вместо MemoryStream для поддержки файлов с диска

			try
			{
				bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
				string? albumCaption = isCaptionTooLong ? null : caption;

				for (int i = 0; i < Math.Min(base64MediaList.Count, 10); i++)
				{
					var item = base64MediaList[i];

					// 1. Умное определение видео (по пути к файлу или Base64 data-uri)
					bool isVideo = item.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
								   item.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
								   item.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) ||
								   item.Contains("video/");

					// 2. Получаем поток (FileStream с диска или MemoryStream из Base64)
					var stream = GetMediaStream(item, out _);
					streams.Add(stream);

					// Подпись задается только для первого элемента альбома
					string? itemCaption = (i == 0 && !string.IsNullOrEmpty(albumCaption)) ? albumCaption : null;
					ParseMode itemParseMode = itemCaption != null ? ParseMode.Html : ParseMode.None;

					IAlbumInputMedia inputMedia;

					if (isVideo)
					{
						// =========================================================================
						// ИЗВЛЕКАЕМ ТОЧНЫЕ РАЗМЕРЫ ВИДЕО ДЛЯ ИДЕАЛЬНЫХ ПРОПОРЦИЙ В АЛЬБОМЕ:
						// =========================================================================
						int width = 0;
						int height = 0;
						int duration = 0;

						if (File.Exists(item))
						{
							var resInfo = await VideoService.GetVideoResolutionAsync(item);
							if (resInfo != null)
							{
								width = resInfo.Width;
								height = resInfo.Height;
								duration = resInfo.DurationSeconds;
							}
						}

						inputMedia = new InputMediaVideo(InputFile.FromStream(stream, $"video_{i}.mp4"))
						{
							Width = width,       // <-- Точная ширина кадра (напр. 1080)
							Height = height,     // <-- Точная высота кадра (напр. 1920)
							Duration = duration, // <-- Длительность
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

				// 3. Отправляем альбом в Telegram
				var messages = await _telegramBotClient.SendMediaGroup(senderId, media);

				// Если текст не поместился в лимит подписи 1024 — отправляем следом отдельным сообщением
				if (isCaptionTooLong)
				{
					await _telegramBotClient.SendMessage(senderId, caption, parseMode: ParseMode.Html);
				}

				// Если к альбому прикреплена кнопка-ссылка
				if (replyMarkup != null && !isCaptionTooLong)
				{
					await _telegramBotClient.SendMessage(senderId, "👇 Ссылка к публикации:", replyMarkup: replyMarkup);
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
				// 4. Гарантированно закрываем все открытые файловые потоки
				foreach (var s in streams)
				{
					s?.Dispose();
				}
			}
		}

		public async Task<Message> SendVoiceAsync(long senderId, string base64Audio, string caption = "", ParseMode parseMode = ParseMode.Html, ReplyMarkup replyMarkup = null)
		{
			string cleanBase64 = base64Audio.Contains(",") ? base64Audio.Split(',')[1] : base64Audio;
			var rawBytes = Convert.FromBase64String(cleanBase64);

			var voiceOggBytes = await VideoService.ConvertToTelegramVoiceOggAsync(rawBytes);

			bool isCaptionTooLong = !string.IsNullOrEmpty(caption) && caption.Length > 1024;
			string? voiceCaption = isCaptionTooLong ? null : caption;

			using var stream = new MemoryStream(voiceOggBytes);

			var voiceMsg = await _telegramBotClient.SendVoice(
				chatId: senderId,
				voice: InputFile.FromStream(stream, "voice.ogg"),
				caption: string.IsNullOrEmpty(voiceCaption) ? null : voiceCaption,
				parseMode: parseMode,
				replyMarkup: isCaptionTooLong ? null : replyMarkup // Если текст длинный, кнопку прикрепим к тексту
			);

			if (isCaptionTooLong)
			{
				await _telegramBotClient.SendMessage(senderId, caption, parseMode: parseMode, replyMarkup: replyMarkup);
			}

			return voiceMsg;
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

		public async Task<Message> SendVideoNoteAsync(long senderId, string base64Video, string caption = "", ParseMode parseMode = ParseMode.Html)
		{
			byte[] rawBytes;
			if (File.Exists(base64Video))
			{
				rawBytes = await File.ReadAllBytesAsync(base64Video);
			}
			else
			{
				string cleanBase64 = base64Video.Contains(",") ? base64Video.Split(',')[1] : base64Video;
				rawBytes = Convert.FromBase64String(cleanBase64);
			}

			var squareVideoBytes = await VideoService.ConvertToTelegramVideoNoteAsync(rawBytes);
			using var stream = new MemoryStream(squareVideoBytes);

			var noteMsg = await _telegramBotClient.SendVideoNote(
				chatId: senderId,
				videoNote: InputFile.FromStream(stream, "circle.mp4"),
				length: 640
			);

			if (!string.IsNullOrWhiteSpace(caption))
			{
				await Task.Delay(500);
				await _telegramBotClient.SendMessage(
					chatId: senderId,
					text: caption,
					parseMode: parseMode
				);
			}

			return noteMsg;
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

		public InlineKeyboardMarkup? BuildInlineButton(string? text, string? url)
		{
			if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(url))
				return null;

			if (!Uri.TryCreate(url, UriKind.Absolute, out _))
				return null;

			return new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl(text, url));
		}

		public async Task<int> GetChatMemberCountAsync(long channelId)
		{
			try
			{
				return await _telegramBotClient.GetChatMemberCount(channelId);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram API] Не удалось получить число подписчиков: {ex.Message}");
				return 0;
			}
		}

		/// <summary>
		/// Быстрый и надежный парсинг постов, просмотров и реакций из публичной витрины Telegram (t.me/s/username)
		/// </summary>
		public async Task<List<TelegramChannelPostDto>> GetPublicChannelPostsAsync(string channelUsername, int subscribersCount)
		{
			var posts = new List<TelegramChannelPostDto>();
			if (string.IsNullOrWhiteSpace(channelUsername)) return posts;

			string cleanUser = channelUsername.Replace("@", "").Trim();
			string url = $"https://t.me/s/{cleanUser}";

			try
			{
				using var client = new HttpClient();
				client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

				var html = await client.GetStringAsync(url);

				// Разбиваем страницу на отдельные блоки постов
				var messageWraps = html.Split(new[] { "js-widget_message_wrap" }, StringSplitOptions.RemoveEmptyEntries);

				foreach (var block in messageWraps)
				{
					// Ищем ID сообщения
					var matchPost = System.Text.RegularExpressions.Regex.Match(block, @"data-post=""[^/]+/(\d+)""");
					if (!matchPost.Success) continue;
					string msgId = matchPost.Groups[1].Value;

					// Просмотры
					int views = 0;
					var matchViews = System.Text.RegularExpressions.Regex.Match(block, @"<span class=""tgme_widget_message_views"">([^<]+)</span>");
					if (matchViews.Success)
					{
						views = ParseTelegramMetric(matchViews.Groups[1].Value);
					}

					// Реакции (суммируем все эмодзи)
					int reactions = 0;
					var matchReactions = System.Text.RegularExpressions.Regex.Matches(block, @"<span class=""[a-zA-Z0-9_]*reaction_count[a-zA-Z0-9_]*"">([^<]+)</span>");
					foreach (System.Text.RegularExpressions.Match rm in matchReactions)
					{
						reactions += ParseTelegramMetric(rm.Groups[1].Value);
					}

					// Дата публикации
					DateTime postDate = DateTime.UtcNow;
					var matchDate = System.Text.RegularExpressions.Regex.Match(block, @"<time datetime=""([^""]+)""");
					if (matchDate.Success && DateTime.TryParse(matchDate.Groups[1].Value, out var dt))
					{
						postDate = dt.ToUniversalTime();
					}

					// Текст публикации
					string? text = null;
					var matchText = System.Text.RegularExpressions.Regex.Match(block, @"<div class=""tgme_widget_message_text[^""]*""[^>]*>([\s\S]*?)</div>");
					if (matchText.Success)
					{
						string rawHtml = matchText.Groups[1].Value;
						// Очищаем HTML теги и декодируем сущности (&quot;, &amp;)
						text = System.Net.WebUtility.HtmlDecode(
							System.Text.RegularExpressions.Regex.Replace(rawHtml, "<.*?>", string.Empty)
						).Trim();
					}

					// Определение медиафайлов
					string mediaType = "TEXT";
					string? mediaUrl = null;

					var matchPhoto = System.Text.RegularExpressions.Regex.Match(block, @"background-image:url\('([^']+)'\)");
					if (matchPhoto.Success)
					{
						mediaUrl = matchPhoto.Groups[1].Value;
						mediaType = block.Contains("tgme_widget_message_video") ? "VIDEO" : "PHOTO";
					}

					// Расчет True ER: отношение просмотров к подписчикам (или реакций к просмотрам)
					double er = views > 0 && reactions > 0
						? Math.Round(((double)reactions / views) * 100.0, 1)
						: (subscribersCount > 0 && views > 0 ? Math.Round(((double)views / subscribersCount) * 100.0, 1) : 0);

					posts.Add(new TelegramChannelPostDto
					{
						MessageId = msgId,
						Text = text,
						Timestamp = postDate,
						Views = views,
						Reactions = reactions,
						MediaType = mediaType,
						MediaUrl = mediaUrl,
						PostUrl = $"https://t.me/{cleanUser}/{msgId}",
						EngagementRate = er
					});
				}

				// Сортируем: свежие посты сверху
				posts = posts.OrderByDescending(p => p.Timestamp).ToList();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Telegram Web Parser] Ошибка парсинга витрины @{cleanUser}: {ex.Message}");
			}

			return posts;
		}

		private int ParseTelegramMetric(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw)) return 0;
			raw = raw.ToUpperInvariant().Trim();

			if (raw.EndsWith("K"))
			{
				if (double.TryParse(raw.Replace("K", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double kVal))
					return (int)(kVal * 1000);
			}
			else if (raw.EndsWith("M"))
			{
				if (double.TryParse(raw.Replace("M", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double mVal))
					return (int)(mVal * 1000000);
			}
			else if (int.TryParse(raw, out int val))
			{
				return val;
			}

			return 0;
		}

		private static Stream GetMediaStream(string input, out bool isFileStream)
		{
			// Если это файл на диске — стримим с диска с расходом RAM 0 МБ!
			if (File.Exists(input))
			{
				isFileStream = true;
				return new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
			}

			// Иначе декодируем Base64
			isFileStream = false;
			string cleanBase64 = input.Contains(",") ? input.Split(',')[1] : input;
			return new MemoryStream(Convert.FromBase64String(cleanBase64));
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
