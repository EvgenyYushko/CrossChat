using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Integrations.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("telegram-system/webhook")]
	public class TelegramSystemWebhookController : ControllerBase
	{
		private readonly AppDbContext _db;
		private readonly IDistributedCache _cache;
		private readonly ILogger<TelegramSystemWebhookController> _logger;
		private readonly ITelegramBotClient _telegramBotClient;
		private readonly IServiceScopeFactory _serviceScopeFactory;
		private readonly ITelegramService _telegramService;

		public TelegramSystemWebhookController(
			AppDbContext db,
			IDistributedCache cache,
			ILogger<TelegramSystemWebhookController> logger,
			ITelegramBotClient telegramBotClient,
			IServiceScopeFactory serviceScopeFactory,
			ITelegramService telegramService
			)
		{
			_db = db;
			_cache = cache;
			_logger = logger;
			_telegramBotClient = telegramBotClient;
			_serviceScopeFactory = serviceScopeFactory;
			_telegramService = telegramService;
		}

		public async Task RunLocalBotListener()
		{
			try
			{
				var stoppingToken = CancellationToken.None;

				var receiverOptions = new ReceiverOptions
				{
					AllowedUpdates = [] // Получать все типы обновлений
				};

				// Запускаем прослушивание с помощью нашего исправленного HandleUpdateAsync
				_telegramBotClient.StartReceiving(
					HandleUpdateAsync,
					HandleErrorAsync,
					receiverOptions,
					stoppingToken
				);

				Console.WriteLine("✅ Локальный Telegram-слушатель успешно запущен!");

				// Бесконечное ожидание работы
				while (!stoppingToken.IsCancellationRequested)
				{
					await Task.Delay(1000, stoppingToken);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		}

		private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
		{
			using (var scope = _serviceScopeFactory.CreateScope())
			{
				var s = scope.ServiceProvider.GetRequiredService<TelegramSystemWebhookController>();
				await s.Receive(update);
			}
		}

		private Task HandleErrorAsync(ITelegramBotClient botClient, Exception error, CancellationToken ct)
		{
			Console.WriteLine(error);
			return Task.CompletedTask;
		}

		[HttpPost]
		public async Task<IActionResult> Receive([FromBody] Update update)
		{
			try
			{
				// ===================================================================
				// 1. СЦЕНАРИЙ: Сообщения боту (/start или /start link_xxxx)
				// ===================================================================
				if (update.Type == UpdateType.Message && update.Message?.Text != null)
				{
					var text = update.Message.Text.Trim();
					var tgUserId = update.Message.From!.Id;

					// А. Обычный вызов /start (если бота нашли через поиск)
					if (text == "/start")
					{
						// Создаем красивую кнопку-ссылку на ваш сайт
						var inlineKeyboard = new InlineKeyboardMarkup(new[]
						{
							InlineKeyboardButton.WithUrl("🌐 Перейти на сайт CrossChat", "https://crosschat.ru")
						});

						await _telegramBotClient.SendMessage(
							chatId: tgUserId,
							text: "👋 <b>Приветствуем в CrossChat!</b>\n\n" +
								  "Я системный бот платформы кроссплатформенного автопостинга и нейро-автоответов.\n\n" +
								  "<b>Как подключить ваш Telegram-канал:</b>\n" +
								  "1. Зайдите в личный кабинет на нашем сайте.\n" +
								  "2. Нажмите кнопку <b>«Привязать Telegram»</b>.\n" +
								  "3. Добавьте меня администратором в ваш канал с правом публикации сообщений.",
							parseMode: ParseMode.Html,
							replyMarkup: inlineKeyboard
						);

						return Ok();
					}

					// Б. Запуск по специальной ссылке с кодом привязки (/start link_xxxx)
					if (text.StartsWith("/start link_"))
					{
						var code = text.Replace("/start link_", "").Trim();
						var userIdStr = await _cache.GetStringAsync($"tg_link:{code}");

						if (!string.IsNullOrEmpty(userIdStr) && int.TryParse(userIdStr, out var userId))
						{
							var user = await _db.Users.FindAsync(userId);
							if (user != null)
							{
								user.TelegramUserId = tgUserId;
								await _db.SaveChangesAsync();

								await _cache.RemoveAsync($"tg_link:{code}");

								await SendBotMessageAsync(tgUserId,
									"✅ <b>Ваш Telegram-аккаунт успешно привязан к CrossChat!</b>\n\nТеперь добавьте бота <b>@cros_hub_bot</b> администратором в ваш канал с правом публикации сообщений.");
								return Ok();
							}
						}
						else
						{
							// Если код устарел (прошло больше 15 минут)
							await SendBotMessageAsync(tgUserId,
								"⚠️ <b>Ссылка привязки устарела или недействительна.</b>\n\nПожалуйста, сгенерируйте новую ссылку в личном кабинете на сайте.");
							return Ok();
						}
					}
				}

				// ===================================================================
				// 2. СЦЕНАРИЙ: Изменение прав или статуса бота в Канале (MyChatMember)
				// ===================================================================
				if (update.Type == UpdateType.MyChatMember && update.MyChatMember != null)
				{
					var chatMember = update.MyChatMember;

					if (chatMember.Chat.Type == ChatType.Channel)
					{
						var channelId = chatMember.Chat.Id;
						var channelTitle = chatMember.Chat.Title ?? "Telegram Канал";
						var channelUsername = chatMember.Chat.Username;
						var addedByTgUserId = chatMember.From.Id; // TgUserId владельца
						var newStatus = chatMember.NewChatMember.Status;

						// -------------------------------------------------------------
						// А. БОТА НАЗНАЧИЛИ АДМИНИСТРАТОРОМ ИЛИ ИЗМЕНИЛИ ЕГО ПРАВА
						// -------------------------------------------------------------
						if (newStatus == ChatMemberStatus.Administrator &&
							chatMember.NewChatMember is ChatMemberAdministrator adminMember)
						{
							// Проверяем главное обязательное право — Публикация сообщений (CanPostMessages)
							bool hasPostingRights = adminMember.CanPostMessages;

							var user = await _db.Users
								.Include(u => u.Profile)
								.FirstOrDefaultAsync(u => u.TelegramUserId == addedByTgUserId);

							if (user != null)
							{
								var activeProfile = user.Profile.FirstOrDefault();
								if (activeProfile != null)
								{
									var existingChannel = await _db.TelegramChannelSettings
										.FirstOrDefaultAsync(c => c.ChannelId == channelId);

									if (existingChannel == null)
									{
										// --- СОЗДАЕМ НОВЫЙ КАНАЛ ---
										var newChannel = new TelegramChannelSettings
										{
											UserId = user.Id,
											ProfileId = activeProfile.Id,
											ChannelId = channelId,
											ChannelTitle = channelTitle,
											ChannelUsername = channelUsername,
											IsActive = hasPostingRights, // Активен ТОЛЬКО если даны права на посты
											ProfilePictureUrl = await _telegramService.GetChannelAvatarBase64Async(channelId)
										};

										_db.TelegramChannelSettings.Add(newChannel);
										await _db.SaveChangesAsync();

										if (hasPostingRights)
										{
											await SendBotMessageAsync(addedByTgUserId,
												$"✅ <b>Канал «{channelTitle}» успешно подключен!</b>\n\nВсе необходимые права выданы. Вы можете настроить автопостинг в личном кабинете CrossChat.");
										}
										else
										{
											await SendBotMessageAsync(addedByTgUserId,
												$"⚠️ <b>Канал «{channelTitle}» добавлен, но боту НЕ ХВАТАЕТ ПРАВ!</b>\n\nПожалуйста, откройте настройки канала и выдайте боту право: <b>«Публикация сообщений»</b> (Can Post Messages).");
										}
									}
									else
									{
										// --- ОБНОВЛЯЕМ ПРАВА СУЩЕСТВУЮЩЕГО КАНАЛА ---
										bool wasActive = existingChannel.IsActive;
										existingChannel.IsActive = hasPostingRights;
										existingChannel.ChannelTitle = channelTitle;
										existingChannel.ChannelUsername = channelUsername;

										await _db.SaveChangesAsync();

										if (!wasActive && hasPostingRights)
										{
											// Права довыдали!
											await SendBotMessageAsync(addedByTgUserId,
												$"✅ <b>Права в канале «{channelTitle}» успешно обновлены!</b>\n\nРазрешение на публикацию сообщений получено. Автопостинг снова активен.");
										}
										else if (wasActive && !hasPostingRights)
										{
											// Права забрали!
											await SendBotMessageAsync(addedByTgUserId,
												$"⚠️ <b>В канале «{channelTitle}» урезаны права бота!</b>\n\nУ бота забрали право на публикацию сообщений. Автопостинг приостановлен до восстановления прав.");
										}
									}
								}
							}
							else
							{
								// Пользователь еще не связал свой аккаунт на сайте
								await SendBotMessageAsync(addedByTgUserId,
									$"⚠️ <b>Бот добавлен в канал «{channelTitle}», но ваш Telegram-аккаунт еще не привязан к сайту!</b>\n\nСначала зайдите в личный кабинет CrossChat и нажмите «Привязать Telegram».");
							}
						}
						// -------------------------------------------------------------
						// Б. БОТА УДАЛИЛИ ИЛИ РАЗЖАЛОВАЛИ ИЗ АДМИНОВ В ОБЫЧНЫЕ ПОДПИСЧИКИ
						// -------------------------------------------------------------
						else if (newStatus == ChatMemberStatus.Kicked ||
								 newStatus == ChatMemberStatus.Left ||
								 newStatus == ChatMemberStatus.Member)
						{
							var existingChannel = await _db.TelegramChannelSettings
								.FirstOrDefaultAsync(c => c.ChannelId == channelId);

							if (existingChannel != null)
							{
								// Очищаем привязанные отложенные посты
								int channelNetTypeId = (int)CrossChat.Integrations.Enums.NetworkType.TelegramChannel;
								var orphanStates = await _db.NetworkStates
									.Where(ns => ns.NetworkType == channelNetTypeId && ns.BotId == existingChannel.Id)
									.ToListAsync();

								if (orphanStates.Any())
								{
									_db.NetworkStates.RemoveRange(orphanStates);
								}

								_db.TelegramChannelSettings.Remove(existingChannel);
								await _db.SaveChangesAsync();

								await SendBotMessageAsync(addedByTgUserId,
									$"❌ <b>Канал «{channelTitle}» был отсоединен.</b>\n\nБот убран из администраторов. Канал и его настройки удалены с сайта.");
							}
						}
					}
				}

				// ===================================================================
				// 3. СЦЕНАРИЙ: Мгновенное обновление названия или фото Канала (ChannelPost)
				// ===================================================================
				if (update.Type == UpdateType.ChannelPost && update.ChannelPost != null)
				{
					var post = update.ChannelPost;
					var channelId = post.Chat.Id;

					// Находим канал в базе данных
					var channel = await _db.TelegramChannelSettings
						.FirstOrDefaultAsync(c => c.ChannelId == channelId);

					if (channel != null)
					{
						bool isUpdated = false;

						// А. МГНОВЕННОЕ ОБНОВЛЕНИЕ НАЗВАНИЯ КАНАЛА
						if (!string.IsNullOrEmpty(post.NewChatTitle) && channel.ChannelTitle != post.NewChatTitle)
						{
							channel.ChannelTitle = post.NewChatTitle;
							isUpdated = true;
							_logger.LogInformation($"[Telegram Webhook] Мгновенно обновлено название канала {channelId}: '{post.NewChatTitle}'");
						}

						// Б. МГНОВЕННОЕ ОБНОВЛЕНИЕ АВАТАРКИ КАНАЛА
						if (post.NewChatPhoto != null && post.NewChatPhoto.Length > 0)
						{
							// Берем самое большое разрешение загруженного фото (последний элемент массива)
							var biggestPhoto = post.NewChatPhoto[^1];

							// Скачиваем аватарку через ваш метод с DownloadFile
							var newBase64 = await _telegramService.GetChannelAvatarBase64ByFileIdAsync(biggestPhoto.FileId);

							if (!string.IsNullOrEmpty(newBase64))
							{
								channel.ProfilePictureUrl = newBase64;
								isUpdated = true;
								_logger.LogInformation($"[Telegram Webhook] Мгновенно обновлена аватарка канала {channelId}");
							}
						}
						// В. МГНОВЕННОЕ УДАЛЕНИЕ АВАТАРКИ КАНАЛА
						else if (post.DeleteChatPhoto == true)
						{
							channel.ProfilePictureUrl = null;
							isUpdated = true;
							_logger.LogInformation($"[Telegram Webhook] Мгновенно удалена аватарка канала {channelId}");
						}

						// Сохраняем изменения в БД
						if (isUpdated)
						{
							await _db.SaveChangesAsync();
						}
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при обработке вебхука системного Telegram бота");
			}

			return Ok();
		}

		// Вспомогательный метод отправки сообщений в ЛС через системного бота
		private async Task SendBotMessageAsync(long tgUserId, string textHtml)
		{
			try
			{
				await _telegramBotClient.SendMessage(tgUserId, textHtml, parseMode: ParseMode.Html);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, $"Не удалось отправить ЛС пользователю {tgUserId} в Telegram");
			}
		}
	}
}