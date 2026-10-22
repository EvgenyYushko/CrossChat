using System.Text.Json;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Data.Entities.Inbox;
using CrossChat.Hubs;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Site;
using CrossChat.Integrations.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Services
{
	public class InboxService : IInboxService
	{
		private readonly AppDbContext _db;
		private readonly IHubContext<InboxHub> _hubContext;
		private readonly IInstagramService _instaService;
		private readonly IFaceBookService _fbService; // <-- 1. ВНЕДРЯЕМ FACEBOOK SERVICE
		private readonly ILogger<InboxService> _logger;
		private readonly SiteSettings _siteSettings;

		public InboxService(
			AppDbContext db,
			IHubContext<InboxHub> hubContext,
			IInstagramService instaService,
			IFaceBookService fbService, // <-- 1. ВНЕДРЯЕМ FACEBOOK SERVICE
			SiteSettings siteSettings,
			ILogger<InboxService> logger)
		{
			_db = db;
			_hubContext = hubContext;
			_instaService = instaService;
			_fbService = fbService;
			_siteSettings = siteSettings;
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
				.FirstOrDefaultAsync(c => c.NetworkType == networkType &&
					c.ExternalUserId == externalUserId &&
					(c.BotId == botId || c.ProfileId == profileId));

			// ===================================================================
			// 1. АВТО-ПОДГРУЗКА АВАТАРКИ И ИМЕНИ ДЛЯ INSTAGRAM
			// ===================================================================
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

			// ===================================================================
			// 2. АВТО-ПОДГРУЗКА ИМЕНИ ДЛЯ FACEBOOK MESSENGER (ТОЛЬКО 1 РАЗ ПРИ СОЗДАНИИ!)
			// ===================================================================
			if (networkType == (int)NetworkType.Facebook &&
				(conversation == null || conversation.FullName == "Пользователь Facebook" || string.IsNullOrEmpty(conversation.FullName)))
			{
				try
				{
					var settings = await _db.FacebookSettings.FindAsync(botId);
					if (settings != null && !string.IsNullOrEmpty(settings.PageAccessToken))
					{
						var fbUser = await _fbService.GetFacebookUserProfileAsync(externalUserId, settings.PageAccessToken);
						if (fbUser != null && !string.IsNullOrWhiteSpace(fbUser.Name))
						{
							fullName = fbUser.Name;
							username = fbUser.Name;
							_logger.LogInformation("👤 [Facebook Profile] Определено настоящее имя клиента: {Name}", fullName);
						}
						else if (string.IsNullOrEmpty(fullName))
						{
							fullName = "Пользователь Facebook";
							username = "Пользователь Facebook";
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Не удалось получить профиль клиента Facebook {UserId}", externalUserId);
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
				if (!string.IsNullOrEmpty(fullName) && fullName != "Пользователь Facebook" && conversation.FullName != fullName)
				{
					conversation.FullName = fullName;
					conversation.Username = fullName;
					isUpdated = true;
				}
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

			conv.IsHumanTakeover = true;
			conv.HumanTakeoverUntil = DateTime.UtcNow.AddHours(2);

			try
			{
				// ===================================================================
				// 1. ОТПРАВКА В СООТВЕТСТВУЮЩУЮ СОЦСЕТЬ
				// ===================================================================
				if ((NetworkType)conv.NetworkType == NetworkType.Instagram)
				{
					var settings = await _db.InstagramSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
						throw new Exception("Настройки Instagram бота не найдены");

					await _instaService.SendMessageAsync(conv.ExternalUserId, text, settings.AccessToken);
				}
				else if ((NetworkType)conv.NetworkType == NetworkType.Facebook)
				{
					// УМНЫЙ ПОИСК С АВТО-ЛЕЧЕНИЕМ
					var settings = await GetFacebookSettingsForConversationAsync(conv);
					if (settings == null || string.IsNullOrEmpty(settings.PageAccessToken))
						throw new Exception("Настройки Facebook страницы не найдены");

					bool ok = await _fbService.SendReplyAsync(conv.ExternalUserId, text, settings.PageAccessToken);
					if (!ok) throw new Exception("Не удалось отправить сообщение в Facebook Messenger");
				}

				// ===================================================================
				// 2. ФИКСИРУЕМ В БАЗЕ ТОЛЬКО ПОСЛЕ УСПЕШНОЙ ОТПРАВКИ!
				// (Никаких фантомных сообщений в превью при сбоях!)
				// ===================================================================
				var message = new InboxMessage
				{
					ConversationId = conv.Id,
					Text = text,
					IsIncoming = false,
					IsSentByBot = false,
					IsReadByManager = true,
					CreatedAtUtc = DateTime.UtcNow
				};

				_db.InboxMessages.Add(message);
				conv.LastMessageSnippet = text;
				conv.LastMessageAt = DateTime.UtcNow;

				await _db.SaveChangesAsync();

				// 3. Пушим в браузер
				await NotifySignalRNewMessage(conv, message);
				return (true, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка при ручной отправке ответа в инбоксе");

				string userFriendlyMessage = ex.Message;
				if (ex.Message.Contains("outside of allowed window") ||
					ex.Message.Contains("2534022") ||
					ex.Message.Contains("2018278") ||
					ex.Message.Contains("2018028") ||
					ex.Message.Contains("вне разрешенного окна"))
				{
					userFriendlyMessage = "OUTSIDE_24H_WINDOW";
				}

				return (false, userFriendlyMessage);
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
					mediaUrl = msg.MediaUrl,
					mediaType = msg.MediaType,
					reaction = msg.Reaction,
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

			// ===================================================================
			// 1. СИНХРОНИЗАЦИЯ ДИАЛОГОВ INSTAGRAM
			// ===================================================================
			var instaAccounts = await _db.InstagramSettings
				.Where(s => s.ProfileId == profileId && !string.IsNullOrEmpty(s.AccessToken) && !string.IsNullOrEmpty(s.InstagramBusinessId))
				.ToListAsync();

			foreach (var account in instaAccounts)
			{
				try
				{
					_logger.LogInformation("🔄 [Inbox Sync] Опрос диалогов Instagram для @{User}...", account.Username);

					var convos = await _instaService.GetRecentConversationsAsync(account.AccessToken!, account.InstagramBusinessId!, 15);

					foreach (var c in convos)
					{
						if (string.IsNullOrEmpty(c.ClientId) || c.ClientId == account.InstagramBusinessId) continue;

						var customer = await _db.InstagramBotCustomers
							.FirstOrDefaultAsync(cust => cust.InstagramSettingsId == account.Id && cust.InstagramSenderId == c.ClientId);

						string username = c.ClientUsername ?? customer?.Username ?? "instagram_user";
						string? avatar = customer?.ProfilePictureUrl;

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

			// ===================================================================
			// 2. СИНХРОНИЗАЦИЯ ДИАЛОГОВ FACEBOOK MESSENGER
			// ===================================================================
			var fbAccounts = await _db.FacebookSettings
				.Where(s => s.ProfileId == profileId && !string.IsNullOrEmpty(s.PageAccessToken) && !string.IsNullOrEmpty(s.PageId))
				.ToListAsync();

			foreach (var fb in fbAccounts)
			{
				try
				{
					_logger.LogInformation("🔄 [Inbox Sync] Опрос диалогов Facebook для '{PageName}'...", fb.PageName);

					// Запрашиваем последние диалоги страницы
					string convsUrl = $"https://graph.facebook.com/v24.0/{fb.PageId}/conversations?platform=messenger&fields=id,updated_time,participants,messages.limit(10){{id,message,from,created_time}}&access_token={fb.PageAccessToken}";

					using var httpClient = new HttpClient();
					var resp = await httpClient.GetAsync(convsUrl);
					if (!resp.IsSuccessStatusCode) continue;

					var json = await resp.Content.ReadAsStringAsync();
					using var doc = JsonDocument.Parse(json);

					if (doc.RootElement.TryGetProperty("data", out var dataArr))
					{
						foreach (var c in dataArr.EnumerateArray())
						{
							string convId = c.GetProperty("id").GetString()!;

							// Находим клиента среди участников диалога
							string? senderId = null;
							string clientName = "Пользователь Facebook";

							if (c.TryGetProperty("participants", out var parts) && parts.TryGetProperty("data", out var pArr))
							{
								foreach (var p in pArr.EnumerateArray())
								{
									var pid = p.GetProperty("id").GetString();
									if (pid != fb.PageId)
									{
										senderId = pid;
										clientName = p.TryGetProperty("name", out var n) ? n.GetString() ?? clientName : clientName;
										break;
									}
								}
							}

							if (string.IsNullOrEmpty(senderId)) continue;

							// Подгружаем аватарку
							var profile = await _fbService.GetFacebookUserProfileAsync(senderId, fb.PageAccessToken!);
							if (profile != null)
							{
								clientName = profile.Name;
							}

							var conversation = await _db.InboxConversations
								.FirstOrDefaultAsync(x => x.NetworkType == (int)NetworkType.Facebook &&
														  x.BotId == fb.Id &&
														  x.ExternalUserId == senderId);

							if (conversation == null)
							{
								conversation = new InboxConversation
								{
									ProfileId = profileId,
									NetworkType = (int)NetworkType.Facebook,
									BotId = fb.Id,
									ExternalChatId = convId,
									ExternalUserId = senderId,
									Username = clientName,
									FullName = clientName,
									AvatarUrl = profile?.ProfilePicUrl,
									LastMessageSnippet = "Диалог синхронизирован",
									LastMessageAt = DateTime.UtcNow,
									CreatedAt = DateTime.UtcNow,
									UnreadCount = 0
								};

								_db.InboxConversations.Add(conversation);
								await _db.SaveChangesAsync();
								totalSynced++;
							}
							else
							{
								if (!string.IsNullOrEmpty(profile?.ProfilePicUrl)) conversation.AvatarUrl = profile.ProfilePicUrl;
								conversation.FullName = clientName;
							}

							// СИНХРОНИЗИРУЕМ ИСТОРИЮ (ДО 10 ПОСЛЕДНИХ СООБЩЕНИЙ)
							if (c.TryGetProperty("messages", out var msgsObj) && msgsObj.TryGetProperty("data", out var mList))
							{
								foreach (var m in mList.EnumerateArray())
								{
									string mid = m.GetProperty("id").GetString()!;
									string text = m.TryGetProperty("message", out var mt) ? mt.GetString() ?? "" : "";
									string fromId = m.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fid) ? fid.GetString() ?? "" : "";
									bool isFromClient = fromId != fb.PageId;

									if (string.IsNullOrEmpty(text)) continue;

									bool msgExists = await _db.InboxMessages.AnyAsync(msg => msg.ExternalMessageId == mid);
									if (!msgExists)
									{
										_db.InboxMessages.Add(new InboxMessage
										{
											ConversationId = conversation.Id,
											ExternalMessageId = mid,
											Text = text,
											IsIncoming = isFromClient,
											IsSentByBot = false,
											IsReadByManager = !isFromClient,
											CreatedAtUtc = DateTime.UtcNow
										});
									}
								}

								var lastOne = mList.EnumerateArray().FirstOrDefault();
								if (lastOne.ValueKind != JsonValueKind.Undefined)
								{
									conversation.LastMessageSnippet = lastOne.TryGetProperty("message", out var mt) ? mt.GetString() : conversation.LastMessageSnippet;
								}
							}

							await _db.SaveChangesAsync();
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка синхронизации Facebook для профиля {ProfileId}", profileId);
				}
			}

			return totalSynced;
		}

		public async Task<bool> SaveEchoMessageAsync(
	int networkType,
	string businessAccountId,
	string customerId,
	string messageId,
	string text,
	string? mediaUrl = null,
	string? mediaType = null)
		{
			try
			{
				int botId = 0;

				if (networkType == (int)NetworkType.Instagram)
				{
					var igSettings = await _db.InstagramSettings
						.AsNoTracking()
						.FirstOrDefaultAsync(s => s.InstagramBusinessId == businessAccountId);
					if (igSettings == null) return false;
					botId = igSettings.Id;
				}
				else if (networkType == (int)NetworkType.Facebook)
				{
					var fbSettings = await _db.FacebookSettings
						.AsNoTracking()
						.FirstOrDefaultAsync(s => s.PageId == businessAccountId);
					if (fbSettings == null) return false;
					botId = fbSettings.Id;
				}

				if (botId == 0) return false;

				// 1. Ищем существующий диалог
				var conversation = await _db.InboxConversations
					.FirstOrDefaultAsync(c => c.NetworkType == networkType &&
											  c.ExternalUserId == customerId &&
											  (c.BotId == botId || c.ExternalChatId == businessAccountId));

				if (conversation == null) return false;

				if (conversation.BotId != botId)
				{
					conversation.BotId = botId;
					conversation.ExternalChatId = businessAccountId;
				}

				// 2. Защита от дублей сообщений, отправленных с нашего сайта за последние 30 секунд
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
					if (string.IsNullOrEmpty(recentSentMessage.ExternalMessageId))
					{
						recentSentMessage.ExternalMessageId = messageId;
						await _db.SaveChangesAsync();
					}
					return false; // Склеено с сообщением с сайта
				}

				if (!string.IsNullOrEmpty(messageId))
				{
					bool alreadyExists = await _db.InboxMessages.AnyAsync(m => m.ExternalMessageId == messageId);
					if (alreadyExists) return false;
				}

				// 3. Сохраняем эхо-сообщение с медиафайлом
				conversation.IsHumanTakeover = true;
				conversation.HumanTakeoverUntil = DateTime.UtcNow.AddHours(2);

				var message = new InboxMessage
				{
					ConversationId = conversation.Id,
					ExternalMessageId = messageId,
					Text = text,
					MediaUrl = mediaUrl,     // <-- СОХРАНЯЕМ ССЫЛКУ НА ФОТО/ВИДЕО
					MediaType = mediaType,   // <-- СОХРАНЯЕМ ТИП (image/video/audio)
					IsIncoming = false,
					IsSentByBot = false,
					IsReadByManager = true,
					CreatedAtUtc = DateTime.UtcNow
				};

				_db.InboxMessages.Add(message);

				conversation.LastMessageSnippet = text;
				conversation.LastMessageAt = DateTime.UtcNow;
				conversation.UnreadCount = 0;

				await _db.SaveChangesAsync();

				_logger.LogInformation("📱 [{Net} Echo] ✅ Медиа/текст с телефона сохранены в диалог {ConvId}: «{Text}»",
					(NetworkType)networkType, conversation.Id, text);

				// Пушим в браузер через SignalR (он сразу отобразит фото/видео/голосовое!)
				await NotifySignalRNewMessage(conversation, message);

				await _hubContext.Clients.Group($"profile_inbox_{conversation.ProfileId}")
					.SendAsync("TakeoverStatusChanged", conversation.Id, true);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка обработки эхо-сообщения с медиа");
				return false;
			}
		}

		public async Task<bool> SendMessageReactionAsync(int messageId, string reaction = "love")
		{
			try
			{
				var msg = await _db.InboxMessages
					.Include(m => m.Conversation)
					.FirstOrDefaultAsync(m => m.Id == messageId);

				if (msg == null || string.IsNullOrEmpty(msg.ExternalMessageId))
					return false;

				var conv = msg.Conversation;

				// ===================================================================
				// 1. ОТПРАВКА РЕАКЦИИ В INSTAGRAM
				// ===================================================================
				if (conv.NetworkType == (int)NetworkType.Instagram)
				{
					var settings = await _db.InstagramSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
						return false;

					bool success = await _instaService.SendReactionAsync(
						conv.ExternalUserId,
						msg.ExternalMessageId,
						reaction,
						settings.AccessToken);

					if (success)
					{
						msg.Reaction = "❤️";
						await _db.SaveChangesAsync();

						_logger.LogInformation("❤️ [Instagram Reaction] Лайк сохранен в БД для сообщения {MsgId}", msg.Id);

						await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
							.SendAsync("UpdateMessageReaction", conv.Id, msg.Id, "❤️");

						return true;
					}
				}
				// ===================================================================
				// 2. ОТПРАВКА РЕАКЦИИ В FACEBOOK MESSENGER
				// ===================================================================
				else if (conv.NetworkType == (int)NetworkType.Facebook)
				{
					var settings = await _db.FacebookSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.PageAccessToken))
						return false;

					// В Messenger передаем реальный эмодзи: если пришел "love" -> ставим "❤️"
					string emojiReaction = (reaction == "love" || string.IsNullOrEmpty(reaction)) ? "❤️" : reaction;

					bool success = await _fbService.SendReactionAsync(
						conv.ExternalUserId,
						msg.ExternalMessageId,
						emojiReaction,
						settings.PageAccessToken);

					if (success)
					{
						msg.Reaction = emojiReaction;
						await _db.SaveChangesAsync();

						_logger.LogInformation("❤️ [Facebook Reaction] Лайк сохранен в БД для сообщения {MsgId}", msg.Id);

						await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
							.SendAsync("UpdateMessageReaction", conv.Id, msg.Id, emojiReaction);

						return true;
					}
				}

				return false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка отправки реакции для сообщения {Id}", messageId);
				return false;
			}
		}

		public async Task<(bool Success, string? NewReaction)> ToggleMessageReactionAsync(int messageId)
		{
			try
			{
				var msg = await _db.InboxMessages
					.Include(m => m.Conversation)
					.FirstOrDefaultAsync(m => m.Id == messageId);

				if (msg == null || string.IsNullOrEmpty(msg.ExternalMessageId))
					return (false, null);

				var conv = msg.Conversation;
				bool isAlreadyLiked = !string.IsNullOrEmpty(msg.Reaction);
				string? newReaction = isAlreadyLiked ? null : "❤️";

				// === INSTAGRAM ===
				if (conv.NetworkType == (int)NetworkType.Instagram)
				{
					var settings = await _db.InstagramSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
						return (false, null);

					string apiReactionParam = isAlreadyLiked ? "" : "love";
					bool success = await _instaService.SendReactionAsync(
						conv.ExternalUserId,
						msg.ExternalMessageId,
						apiReactionParam,
						settings.AccessToken);

					if (success)
					{
						msg.Reaction = newReaction;
						await _db.SaveChangesAsync();

						await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
							.SendAsync("UpdateMessageReaction", conv.Id, msg.Id, newReaction);

						return (true, newReaction);
					}
				}
				// === FACEBOOK MESSENGER ===
				else if (conv.NetworkType == (int)NetworkType.Facebook)
				{
					// ИСПОЛЬЗУЕМ УМНЫЙ ПОИСК
					var settings = await GetFacebookSettingsForConversationAsync(conv);
					if (settings == null || string.IsNullOrEmpty(settings.PageAccessToken))
						return (false, null);

					bool success = await _fbService.SendReactionAsync(
						conv.ExternalUserId,
						msg.ExternalMessageId,
						newReaction,
						settings.PageAccessToken);

					if (success)
					{
						msg.Reaction = newReaction;
						await _db.SaveChangesAsync();

						await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
							.SendAsync("UpdateMessageReaction", conv.Id, msg.Id, newReaction);

						return (true, newReaction);
					}
				}

				return (false, msg.Reaction);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка переключения реакции для сообщения {Id}", messageId);
				return (false, null);
			}
		}

		public async Task<bool> HandleReactionWebhookAsync(string businessAccountId, string externalMessageId, string? emoji, string? action)
		{
			try
			{
				var msg = await _db.InboxMessages
					.Include(m => m.Conversation)
					.FirstOrDefaultAsync(m => m.ExternalMessageId == externalMessageId);

				if (msg == null) return false;

				string? updatedReaction = null;
				if (action == "react")
				{
					updatedReaction = !string.IsNullOrEmpty(emoji) ? emoji : "❤️";
				}
				else if (action == "unreact")
				{
					updatedReaction = null;
				}

				msg.Reaction = updatedReaction;
				await _db.SaveChangesAsync();

				_logger.LogInformation("📱 [Reaction Webhook] Реакция синхронизирована для msg {Id}: {React}", msg.Id, updatedReaction ?? "СНЯТА");

				await _hubContext.Clients.Group($"profile_inbox_{msg.Conversation.ProfileId}")
					.SendAsync("UpdateMessageReaction", msg.ConversationId, msg.Id, updatedReaction);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка синхронизации реакции из вебхука");
				return false;
			}
		}

		public async Task<bool> DeleteMessageByExternalIdAsync(string externalMessageId)
		{
			try
			{
				var msg = await _db.InboxMessages
					.Include(m => m.Conversation)
					.FirstOrDefaultAsync(m => m.ExternalMessageId == externalMessageId);

				if (msg == null) return false;

				var conv = msg.Conversation;
				int convId = conv.Id;
				int msgId = msg.Id;
				int profileId = conv.ProfileId;

				_db.InboxMessages.Remove(msg);

				var previousMsg = await _db.InboxMessages
					.Where(m => m.ConversationId == convId && m.Id != msgId)
					.OrderByDescending(m => m.CreatedAtUtc)
					.FirstOrDefaultAsync();

				conv.LastMessageSnippet = previousMsg?.Text ?? "";
				conv.LastMessageAt = previousMsg?.CreatedAtUtc ?? conv.CreatedAt;

				await _db.SaveChangesAsync();

				await _hubContext.Clients.Group($"profile_inbox_{profileId}")
					.SendAsync("MessageDeleted", convId, msgId, conv.LastMessageSnippet);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка удаления сообщения по ExternalId");
				return false;
			}
		}

		public async Task<bool> DeleteMessageFromInboxAsync(int messageId)
		{
			try
			{
				var msg = await _db.InboxMessages
					.Include(m => m.Conversation)
					.FirstOrDefaultAsync(m => m.Id == messageId);

				if (msg == null) return false;

				var conv = msg.Conversation;
				int convId = conv.Id;
				int profileId = conv.ProfileId;

				_db.InboxMessages.Remove(msg);

				var previousMsg = await _db.InboxMessages
					.Where(m => m.ConversationId == convId && m.Id != messageId)
					.OrderByDescending(m => m.CreatedAtUtc)
					.FirstOrDefaultAsync();

				conv.LastMessageSnippet = previousMsg?.Text ?? "";
				conv.LastMessageAt = previousMsg?.CreatedAtUtc ?? conv.CreatedAt;

				await _db.SaveChangesAsync();

				await _hubContext.Clients.Group($"profile_inbox_{profileId}")
					.SendAsync("MessageDeleted", convId, messageId, conv.LastMessageSnippet);

				return true;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка удаления сообщения менеджером");
				return false;
			}
		}

		public async Task<(bool Success, string? ErrorMessage)> SendManagerMediaReplyAsync(int conversationId, IFormFile file)
		{
			var conv = await _db.InboxConversations.FirstOrDefaultAsync(c => c.Id == conversationId);
			if (conv == null) return (false, "Диалог не найден");

			try
			{
				string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
				string mediaType = "image";
				string snippetLabel = "📷 Фотография";

				// 1. АУДИО
				if (file.ContentType.StartsWith("audio/") ||
					file.FileName.StartsWith("voice_") ||
					ext == ".mp3" || ext == ".ogg" || ext == ".wav" || ext == ".m4a" || ext == ".webm")
				{
					mediaType = "audio";
					snippetLabel = "🎙️ Голосовое сообщение";
					ext = ".m4a";
				}
				// 2. ВИДЕО
				else if (file.ContentType.StartsWith("video/") || ext == ".mp4" || ext == ".mov" || ext == ".avi")
				{
					mediaType = "video";
					snippetLabel = "🎬 Видеозапись";
					ext = ".mp4";
				}

				// 3. Сохранение исходного файла
				string tempFolder = _siteSettings.TempFolder;
				if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

				string tempFileName = $"{Guid.NewGuid()}{ext}";
				string localPath = Path.Combine(tempFolder, tempFileName);

				using (var stream = new FileStream(localPath, FileMode.Create))
				{
					await file.CopyToAsync(stream);
				}

				// 4. ОБРАБОТКА МЕДИАФАЙЛОВ
				if (mediaType == "video")
				{
					await VideoService.CompressVideoForDirectAsync(localPath, _logger, async (percent, statusText) =>
					{
						try
						{
							await _hubContext.Clients.Group($"profile_inbox_{conv.ProfileId}")
								.SendAsync("ServerProcessingProgress", new
								{
									conversationId = conv.Id,
									percent = percent,
									text = statusText
								});
						}
						catch { }
					});
				}
				else if (mediaType == "audio")
				{
					string convertedAudioPath = await VideoService.ConvertToInstagramAudioAsync(localPath, _logger);
					tempFileName = Path.GetFileName(convertedAudioPath);
				}

				string publicUrl = $"{_siteSettings.AppUrl.TrimEnd('/')}/temp_media/{tempFileName}";

				// 5. Включаем Human Takeover
				conv.IsHumanTakeover = true;
				conv.HumanTakeoverUntil = DateTime.UtcNow.AddHours(2);

				// 6. ОТПРАВКА В ЗАВИСИМОСТИ ОТ СОЦСЕТИ
				if ((NetworkType)conv.NetworkType == NetworkType.Instagram)
				{
					var settings = await _db.InstagramSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
						return (false, "Настройки Instagram бота не найдены");

					await _instaService.SendMediaAttachmentAsync(conv.ExternalUserId, mediaType, publicUrl, settings.AccessToken);
				}
				// === FACEBOOK MESSENGER ===
				else if ((NetworkType)conv.NetworkType == NetworkType.Facebook)
				{
					var settings = await _db.FacebookSettings.FindAsync(conv.BotId);
					if (settings == null || string.IsNullOrEmpty(settings.PageAccessToken))
						return (false, "Настройки Facebook страницы не найдены");

					bool ok = await _fbService.SendMediaAttachmentAsync(conv.ExternalUserId, mediaType, publicUrl, settings.PageAccessToken);
					if (!ok) return (false, "Ошибка отправки медиафайла в Facebook Messenger");
				}

				// 7. Сохранение сообщения в БД
				var message = new InboxMessage
				{
					ConversationId = conv.Id,
					Text = snippetLabel,
					MediaUrl = publicUrl,
					MediaType = mediaType,
					IsIncoming = false,
					IsSentByBot = false,
					IsReadByManager = true,
					CreatedAtUtc = DateTime.UtcNow
				};

				_db.InboxMessages.Add(message);
				conv.LastMessageSnippet = snippetLabel;
				conv.LastMessageAt = DateTime.UtcNow;

				await _db.SaveChangesAsync();

				// 8. Пушим в SignalR
				await NotifySignalRNewMessage(conv, message);

				return (true, null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Ошибка отправки медиафайла менеджером");

				string userFriendlyMessage = ex.Message;
				// 2018278 - это точный сабкод закрытого окна Facebook Messenger!
				if (ex.Message.Contains("outside of allowed window") ||
					ex.Message.Contains("2534022") ||
					ex.Message.Contains("2018278") ||
					ex.Message.Contains("2018028") ||
					ex.Message.Contains("вне разрешенного окна"))
				{
					userFriendlyMessage = "OUTSIDE_24H_WINDOW";
				}
				return (false, userFriendlyMessage);

			}
		}

		private async Task<FacebookSettings?> GetFacebookSettingsForConversationAsync(InboxConversation conv)
		{
			// 1. Пробуем найти по сохраненному BotId
			var settings = await _db.FacebookSettings.FindAsync(conv.BotId);
			if (settings != null && !string.IsNullOrEmpty(settings.PageAccessToken))
				return settings;

			// 2. Если аккаунт переподключали и Id сменился — ищем по PageId (ExternalChatId)
			if (!string.IsNullOrEmpty(conv.ExternalChatId))
			{
				settings = await _db.FacebookSettings
					.FirstOrDefaultAsync(s => s.PageId == conv.ExternalChatId && !string.IsNullOrEmpty(s.PageAccessToken));
			}

			// 3. Или берем первую доступную страницу Facebook в рамках этого же профиля
			if (settings == null)
			{
				settings = await _db.FacebookSettings
					.FirstOrDefaultAsync(s => s.ProfileId == conv.ProfileId && s.IsActive && !string.IsNullOrEmpty(s.PageAccessToken));
			}

			// АВТО-ЛЕЧЕНИЕ: если нашли актуальные настройки — чиним диалог прямо в базе!
			if (settings != null)
			{
				conv.BotId = settings.Id;
				conv.ExternalChatId = settings.PageId;
				await _db.SaveChangesAsync();
			}

			return settings;
		}
	}
}