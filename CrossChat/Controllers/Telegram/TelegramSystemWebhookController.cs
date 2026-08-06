using CrossChat.Data;
using CrossChat.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

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

		public TelegramSystemWebhookController(
			AppDbContext db,
			IDistributedCache cache,
			ILogger<TelegramSystemWebhookController> logger,
			ITelegramBotClient telegramBotClient)
		{
			_db = db;
			_cache = cache;
			_logger = logger;
			_telegramBotClient = telegramBotClient;
		}

		public async Task RunLocalBotListener()
		{
			try
			{
				// Получаем информацию о боте
				var stoppingToken = CancellationToken.None;
				var me = await _telegramBotClient.SendRequest(new GetMeRequest(), stoppingToken);

				// Минимальная настройка ReceiverOptions
				var receiverOptions = new ReceiverOptions
				{
					AllowedUpdates = []
				};

				// Базовая версия StartReceiving
				_telegramBotClient.StartReceiving(
					HandleUpdateAsync,
					HandleErrorAsync,
					receiverOptions,
					stoppingToken
				);

				Console.WriteLine("Бот начал работу");

				// Бесконечное ожидание
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

		private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
		{
			return Receive(update);
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
				// 1. СЦЕНАРИЙ: Пользователь привязывает свой Telegram аккаунт (/start link_xxx)
				// ===================================================================
				if (update.Type == UpdateType.Message && update.Message?.Text != null)
				{
					var text = update.Message.Text.Trim();
					var tgUserId = update.Message.From!.Id;

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

								// Удаляем использованный код из кэша
								await _cache.RemoveAsync($"tg_link:{code}");

								// Отправляем ответ в Telegram пользователю
								//var bot = new TelegramBotClient("ВАШ_ТОКЕН_CROS_HUB_BOT");
								await _telegramBotClient.SendMessage(tgUserId, "✅ Ваш Telegram-аккаунт успешно привязан к CrossChat!\n\nТеперь вы можете добавить бота администратором в ваш канал.");
								return Ok();
							}
						}
					}
				}

				// ===================================================================
				// СЦЕНАРИЙ: Изменение статуса бота в Канале (MyChatMember)
				// ===================================================================
				if (update.Type == UpdateType.MyChatMember && update.MyChatMember != null)
				{
					var chatMember = update.MyChatMember;

					// Проверяем, что событие происходит в Канале
					if (chatMember.Chat.Type == ChatType.Channel)
					{
						var channelId = chatMember.Chat.Id;
						var channelTitle = chatMember.Chat.Title ?? "Telegram Канал";
						var newStatus = chatMember.NewChatMember.Status;

						// 1. БОТА НАЗНАЧИЛИ АДМИНИСТРАТОРОМ -> Добавляем/Активируем канал на сайте
						if (newStatus == ChatMemberStatus.Administrator)
						{
							var addedByTgUserId = chatMember.From.Id;

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
										var newChannel = new TelegramChannelSettings
										{
											UserId = user.Id,
											ProfileId = activeProfile.Id,
											ChannelId = channelId,
											ChannelTitle = channelTitle,
											ChannelUsername = chatMember.Chat.Username,
											IsActive = true
										};

										_db.TelegramChannelSettings.Add(newChannel);
										await _db.SaveChangesAsync();

										_logger.LogInformation($"[Telegram Channel] Канал '{channelTitle}' ({channelId}) автоматически ДОБАВЛЕН для пользователя {user.Id}");
									}
								}
							}
						}
						// 2. БОТА УДАЛИЛИ ИЛИ РАЗЖАЛОВАЛИ ИЗ АДМИНОВ -> Автоматически удаляем канал с сайта!
						else if (newStatus == ChatMemberStatus.Kicked || 
						         newStatus == ChatMemberStatus.Left || 
						         newStatus == ChatMemberStatus.Member)
						{
							var existingChannel = await _db.TelegramChannelSettings
								.FirstOrDefaultAsync(c => c.ChannelId == channelId);

							if (existingChannel != null)
							{
								// Очищаем запланированные публикации для этого канала в NetworkStates
								int channelNetTypeId = (int)CrossChat.Integrations.Enums.NetworkType.TelegramPublic;
								var orphanStates = await _db.NetworkStates
									.Where(ns => ns.NetworkType == channelNetTypeId && ns.BotId == existingChannel.Id)
									.ToListAsync();

								if (orphanStates.Any())
								{
									_db.NetworkStates.RemoveRange(orphanStates);
								}

								// Удаляем сам канал из базы данных
								_db.TelegramChannelSettings.Remove(existingChannel);
								await _db.SaveChangesAsync();

								_logger.LogInformation($"[Telegram Channel] Канал '{channelTitle}' ({channelId}) автоматически УДАЛЕН из БД, так как бот был убран из админов.");
							}
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
	}

	public class GetMeRequest : IRequest<Telegram.Bot.Types.User>
	{
		public HttpMethod HttpMethod => HttpMethod.Get;
		public string MethodName => "getMe";
		public bool IsWebhookResponse { get; set; }

		public HttpContent? ToHttpContent() => null;
	}
}