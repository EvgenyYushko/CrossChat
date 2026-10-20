using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Data.Entities.Inbox;
using CrossChat.Hubs;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models; // <-- Подключаем DTO
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Services
{
	public class InboxService : IInboxService
	{
		private readonly AppDbContext _db;
		private readonly IHubContext<InboxHub> _hubContext;
		private readonly IInstagramService _instaService;
		private readonly ILogger<InboxService> _logger;

		public InboxService(
			AppDbContext db,
			IHubContext<InboxHub> hubContext,
			IInstagramService instaService,
			ILogger<InboxService> logger)
		{
			_db = db;
			_hubContext = hubContext;
			_instaService = instaService;
			_logger = logger;
		}

		public async Task<InboxConversationDto> GetOrCreateConversationAsync(
			int profileId,
			int networkType,
			int botId,
			string externalUserId,
			string? username,
			string? fullName,
			string? avatarUrl)
		{
			var conversation = await _db.InboxConversations
				.FirstOrDefaultAsync(c => c.NetworkType == networkType && c.BotId == botId && c.ExternalUserId == externalUserId);

			// АВТО-ПОДГРУЗКА АВАТАРКИ И ЮЗЕРНЕЙМА ДЛЯ INSTAGRAM, ЕСЛИ ИХ НЕТ:
			if ((string.IsNullOrEmpty(avatarUrl) || username == "instagram_user" || string.IsNullOrEmpty(username))
				&& networkType == (int)NetworkType.Instagram)
			{
				try
				{
					var settings = await _db.InstagramSettings.FindAsync(botId);
					if (settings != null && !string.IsNullOrEmpty(settings.AccessToken))
					{
						var userProfile = await _instaService.GetInstagramUserProfileAsync(externalUserId, settings.AccessToken);
						if (userProfile != null)
						{
							if (!string.IsNullOrEmpty(userProfile.Username)) username = userProfile.Username;
							fullName = !string.IsNullOrEmpty(userProfile.Name) ? userProfile.Name : username;
							if (!string.IsNullOrEmpty(userProfile.ProfilePicUrl)) avatarUrl = userProfile.ProfilePicUrl;

							// Сохраняем в кэш клиентов
							var customer = await _db.InstagramBotCustomers
								.FirstOrDefaultAsync(c => c.InstagramSettingsId == botId && c.InstagramSenderId == externalUserId);

							if (customer != null)
							{
								customer.Username = username;
								customer.ProfilePictureUrl = avatarUrl;
								await _db.SaveChangesAsync();
							}
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось получить профиль клиента Instagram {UserId}", externalUserId);
				}
			}

			if (conversation == null)
			{
				conversation = new InboxConversation
				{
					ProfileId = profileId,
					NetworkType = networkType,
					BotId = botId,
					ExternalUserId = externalUserId,
					Username = username,
					FullName = fullName ?? username,
					AvatarUrl = avatarUrl,
					CreatedAt = DateTime.UtcNow,
					LastMessageAt = DateTime.UtcNow
				};

				_db.InboxConversations.Add(conversation);
				await _db.SaveChangesAsync();
			}
			else
			{
				bool isUpdated = false;
				if (!string.IsNullOrEmpty(username) && conversation.Username != username) { conversation.Username = username; isUpdated = true; }
				if (!string.IsNullOrEmpty(avatarUrl) && conversation.AvatarUrl != avatarUrl) { conversation.AvatarUrl = avatarUrl; isUpdated = true; }
				if (isUpdated) await _db.SaveChangesAsync();
			}

			return new InboxConversationDto
			{
				Id = conversation.Id,
				ProfileId = conversation.ProfileId,
				NetworkType = conversation.NetworkType,
				BotId = conversation.BotId,
				ExternalUserId = conversation.ExternalUserId,
				Username = conversation.Username,
				FullName = conversation.FullName,
				AvatarUrl = conversation.AvatarUrl,
				IsHumanTakeover = conversation.IsHumanTakeover,
				HumanTakeoverUntil = conversation.HumanTakeoverUntil
			};
		}

		public async Task<bool> SaveIncomingMessageAsync(
			int conversationId,
			string externalMsgId,
			string text,
			string? mediaUrl = null,
			string? mediaType = null)
		{
			if (!string.IsNullOrEmpty(externalMsgId))
			{
				bool exists = await _db.InboxMessages.AnyAsync(m => m.ExternalMessageId == externalMsgId);
				if (exists) return false;
			}

			var conversation = await _db.InboxConversations.FindAsync(conversationId);
			if (conversation == null) return false;

			var message = new InboxMessage
			{
				ConversationId = conversation.Id,
				ExternalMessageId = externalMsgId,
				Text = text,
				IsIncoming = true,
				IsSentByBot = false,
				MediaUrl = mediaUrl,
				MediaType = mediaType,
				CreatedAtUtc = DateTime.UtcNow
			};

			_db.InboxMessages.Add(message);

			conversation.LastMessageSnippet = text;
			conversation.LastMessageAt = DateTime.UtcNow;
			conversation.UnreadCount++;

			await _db.SaveChangesAsync();

			await NotifySignalRNewMessage(conversation, message);
			return true;
		}

		public async Task<bool> SaveBotReplyAsync(int conversationId, string text)
		{
			var conversation = await _db.InboxConversations.FindAsync(conversationId);
			if (conversation == null) return false;

			var message = new InboxMessage
			{
				ConversationId = conversation.Id,
				Text = text,
				IsIncoming = false,
				IsSentByBot = true,
				IsReadByManager = true,
				CreatedAtUtc = DateTime.UtcNow
			};

			_db.InboxMessages.Add(message);

			conversation.LastMessageSnippet = text;
			conversation.LastMessageAt = DateTime.UtcNow;

			await _db.SaveChangesAsync();

			await NotifySignalRNewMessage(conversation, message);
			return true;
		}

		public async Task<(bool Success, string? ErrorMessage)> SendManagerReplyAsync(int conversationId, string text)
		{
			var conv = await _db.InboxConversations.FirstOrDefaultAsync(c => c.Id == conversationId);
			if (conv == null) return (false, "Диалог не найден");

			try
			{
				// Включаем Human Takeover на 2 часа
				conv.IsHumanTakeover = true;
				conv.HumanTakeoverUntil = DateTime.UtcNow.AddHours(2);

				// Отправка в Instagram
				if ((NetworkType)conv.NetworkType == NetworkType.Instagram)
				{
					var settings = await _db.InstagramSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
						return (false, "Настройки Instagram бота не найдены");

					await _instaService.SendMessageAsync(conv.ExternalUserId, text, settings.AccessToken);
				}

				var message = new InboxMessage
				{
					ConversationId = conv.Id,
					Text = text,
					IsIncoming = false,
					IsSentByBot = false, // Отправил человек
					IsReadByManager = true,
					CreatedAtUtc = DateTime.UtcNow
				};

				_db.InboxMessages.Add(message);
				conv.LastMessageSnippet = text;
				conv.LastMessageAt = DateTime.UtcNow;

				await _db.SaveChangesAsync();

				await NotifySignalRNewMessage(conv, message);
				return (true, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при ручной отправке ответа в инбоксе");

				// Распознаем 24-часовое окно Meta
				if (ex.Message.Contains("outside of allowed window") || ex.Message.Contains("2534022"))
				{
					return (false, "OUTSIDE_24H_WINDOW");
				}

				return (false, ex.Message);
			}
		}

		public async Task<bool> ToggleHumanTakeoverAsync(int conversationId, bool isTakeover)
		{
			var conv = await _db.InboxConversations.FirstOrDefaultAsync(c => c.Id == conversationId);
			if (conv == null) return false;

			conv.IsHumanTakeover = isTakeover;
			conv.HumanTakeoverUntil = isTakeover ? DateTime.UtcNow.AddHours(2) : null;

			await _db.SaveChangesAsync();

			await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
				.SendAsync("TakeoverStatusChanged", conv.Id, isTakeover);

			return true;
		}

		private async Task NotifySignalRNewMessage(InboxConversation conv, InboxMessage msg)
		{
			try
			{
				var payload = new
				{
					conversationId = conv.Id,
					messageId = msg.Id,
					text = msg.Text,
					isIncoming = msg.IsIncoming,
					isSentByBot = msg.IsSentByBot,
					time = msg.CreatedAtUtc.ToString("HH:mm"),
					lastSnippet = conv.LastMessageSnippet,
					unreadCount = conv.UnreadCount
				};

				await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
					.SendAsync("ReceiveInboxMessage", payload);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Не удалось отправить SignalR оповещение");
			}
		}

		public async Task<int> SyncProfileConversationsAsync(int profileId)
		{
			int totalSynced = 0;

			var instaAccounts = await _db.InstagramSettings
				.Where(s => s.ProfileId == profileId && s.IsActive && !string.IsNullOrEmpty(s.AccessToken) && !string.IsNullOrEmpty(s.InstagramBusinessId))
				.ToListAsync();

			foreach (var account in instaAccounts)
			{
				try
				{
					_logger.LogInformation("🔄 [Inbox Sync] Опрос диалогов Instagram для @{User}...", account.Username);

					// ПЕРЕДАЕМ InstagramBusinessId ДЛЯ ФИЛЬТРАЦИИ САМОГО СЕБЯ:
					var convos = await _instaService.GetRecentConversationsAsync(account.AccessToken!, account.InstagramBusinessId!, 15);

					foreach (var c in convos)
					{
						if (string.IsNullOrEmpty(c.ClientId) || c.ClientId == account.InstagramBusinessId) continue;

						var customer = await _db.InstagramBotCustomers
							.FirstOrDefaultAsync(cust => cust.InstagramSettingsId == account.Id && cust.InstagramSenderId == c.ClientId);

						string username = c.ClientUsername ?? customer?.Username ?? "instagram_user";
						string? avatar = customer?.ProfilePictureUrl;

						// Если данных нет — запрашиваем профиль
						if (string.IsNullOrEmpty(avatar) || username == "instagram_user")
						{
							try
							{
								var userProfile = await _instaService.GetInstagramUserProfileAsync(c.ClientId, account.AccessToken!);
								if (userProfile != null)
								{
									if (!string.IsNullOrEmpty(userProfile.Username)) username = userProfile.Username;
									if (!string.IsNullOrEmpty(userProfile.ProfilePicUrl)) avatar = userProfile.ProfilePicUrl;

									if (customer == null)
									{
										customer = new InstagramBotCustomer
										{
											InstagramSettingsId = account.Id,
											InstagramSenderId = c.ClientId,
											Username = username,
											ProfilePictureUrl = avatar
										};
										_db.InstagramBotCustomers.Add(customer);
									}
									else
									{
										customer.Username = username;
										customer.ProfilePictureUrl = avatar;
									}
									await _db.SaveChangesAsync();
								}
							}
							catch { }
						}

						var conversation = await _db.InboxConversations
							.FirstOrDefaultAsync(x => x.NetworkType == (int)NetworkType.Instagram &&
													  x.BotId == account.Id &&
													  x.ExternalUserId == c.ClientId);

						if (conversation == null)
						{
							conversation = new InboxConversation
							{
								ProfileId = profileId,
								NetworkType = (int)NetworkType.Instagram,
								BotId = account.Id,
								ExternalChatId = c.ConversationId,
								ExternalUserId = c.ClientId,
								Username = username,
								FullName = username,
								AvatarUrl = avatar,
								LastMessageSnippet = c.LastMessageText ?? "[Вложение]",
								LastMessageAt = c.LastMessageTime,
								CreatedAt = DateTime.UtcNow,
								UnreadCount = c.IsLastMessageFromClient ? 1 : 0
							};

							_db.InboxConversations.Add(conversation);
							await _db.SaveChangesAsync();
							totalSynced++;
						}
						else
						{
							if (!string.IsNullOrEmpty(avatar)) conversation.AvatarUrl = avatar;
							if (!string.IsNullOrEmpty(username)) conversation.Username = username;
							conversation.LastMessageSnippet = c.LastMessageText ?? conversation.LastMessageSnippet;
							conversation.LastMessageAt = c.LastMessageTime;
						}

						if (!string.IsNullOrEmpty(c.LastMessageId))
						{
							bool msgExists = await _db.InboxMessages.AnyAsync(m => m.ExternalMessageId == c.LastMessageId);
							if (!msgExists && !string.IsNullOrEmpty(c.LastMessageText))
							{
								_db.InboxMessages.Add(new InboxMessage
								{
									ConversationId = conversation.Id,
									ExternalMessageId = c.LastMessageId,
									Text = c.LastMessageText,
									IsIncoming = c.IsLastMessageFromClient,
									IsSentByBot = !c.IsLastMessageFromClient,
									CreatedAtUtc = c.LastMessageTime,
									IsReadByManager = !c.IsLastMessageFromClient
								});
							}
						}

						await _db.SaveChangesAsync();
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка синхронизации Instagram для профиля {ProfileId}", profileId);
				}
			}

			return totalSynced;
		}

		public async Task<bool> SaveEchoMessageAsync(
			int networkType,
			string businessAccountId,
			string customerId,
			string messageId,
			string text)
		{
			try
			{
				// 1. Ищем настройки аккаунта
				var settings = await _db.InstagramSettings
					.AsNoTracking()
					.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);

				if (settings == null) return false;

				// 2. Ищем существующий диалог
				var conversation = await _db.InboxConversations
					.FirstOrDefaultAsync(c => c.NetworkType == networkType && c.BotId == settings.Id && c.ExternalUserId == customerId);

				if (conversation == null) return false;

				// ==================================================================================
				// 3. ЗАЩИТА ОТ ДУБЛИРОВАНИЯ (СКЛЕЙКА С СООБЩЕНИЕМ, ОТПРАВЛЕННЫМ С САЙТА):
				// Ищем наше собственное исходящее сообщение с таким же текстом за последние 30 секунд
				// ==================================================================================
				var thresholdUtc = DateTime.UtcNow.AddSeconds(-30);
				var recentSentMessage = await _db.InboxMessages
					.Where(m => m.ConversationId == conversation.Id &&
								!m.IsIncoming &&
								m.Text == text &&
								m.CreatedAtUtc >= thresholdUtc)
					.OrderByDescending(m => m.CreatedAtUtc)
					.FirstOrDefaultAsync();

				if (recentSentMessage != null)
				{
					// Это подтверждение нашего сообщения с сайта!
					// Привязываем официальный ID от Meta и НЕ создаем дубль!
					if (string.IsNullOrEmpty(recentSentMessage.ExternalMessageId))
					{
						recentSentMessage.ExternalMessageId = messageId;
						await _db.SaveChangesAsync();
					}
					return false; // ВЫХОДИМ: второй раз в базу не пишем и в SignalR не шлем!
				}

				// Защита от дублей по MessageId (если вебхук прилетел повторно)
				if (!string.IsNullOrEmpty(messageId))
				{
					bool alreadyExists = await _db.InboxMessages.AnyAsync(m => m.ExternalMessageId == messageId);
					if (alreadyExists) return false;
				}

				// ==================================================================================
				// 4. ЕСЛИ ДОШЛИ СЮДА — ЭТО РЕАЛЬНОЕ СООБЩЕНИЕ, ОТПРАВЛЕННОЕ С ТЕЛЕФОНА!
				// ==================================================================================
				conversation.IsHumanTakeover = true;
				conversation.HumanTakeoverUntil = DateTime.UtcNow.AddHours(2);

				var message = new InboxMessage
				{
					ConversationId = conversation.Id,
					ExternalMessageId = messageId,
					Text = text,
					IsIncoming = false,
					IsSentByBot = false, // Отправил человек с телефона
					IsReadByManager = true,
					CreatedAtUtc = DateTime.UtcNow
				};

				_db.InboxMessages.Add(message);

				conversation.LastMessageSnippet = text;
				conversation.LastMessageAt = DateTime.UtcNow;
				conversation.UnreadCount = 0;

				await _db.SaveChangesAsync();

				_logger.LogInformation("📱 [Echo] Зафиксирован ответ менеджера с телефона клиенту {CustomerId}: «{Text}»", customerId, text);

				// Пушим в SignalR на сайт (теперь ровно 1 раз!)
				await NotifySignalRNewMessage(conversation, message);

				await _hubContext.Clients.Group($"profile_inbox_{conversation.ProfileId}")
					.SendAsync("TakeoverStatusChanged", conversation.Id, true);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка обработки эхо-сообщения");
				return false;
			}
		}
	}
}