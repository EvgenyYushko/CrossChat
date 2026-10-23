using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyReplyConsumer : IConsumer<BlueSkyProcessReply>
	{
		private readonly AppDbContext _db;
		private readonly IBlueSkyService _bskyService;
		private readonly IAiService _aiService;
		private readonly IDatabase _redis;
		private readonly IBlueSkyConsole _console;
		private readonly ILogger<BlueSkyReplyConsumer> _logger;
		private readonly IBlueSkyTokenManager _tokenManager;
		private readonly IInboxService _inboxService;

		public BlueSkyReplyConsumer(
			AppDbContext db,
			IBlueSkyService bskyService,
			IAiService aiService,
			IConnectionMultiplexer redis,
			IBlueSkyConsole console,
			ILogger<BlueSkyReplyConsumer> logger,
			IBlueSkyTokenManager tokenManager,
			IInboxService inboxService) // <-- ВНЕДРЯЕМ
		{
			_db = db;
			_bskyService = bskyService;
			_aiService = aiService;
			_redis = redis.GetDatabase();
			_console = console;
			_logger = logger;
			_tokenManager = tokenManager;
			_inboxService = inboxService; // <-- СОХРАНЯЕМ
		}

		public async Task Consume(ConsumeContext<BlueSkyProcessReply> context)
		{
			var msg = context.Message;

			var botModel = await _tokenManager.GetValidTokenAsync(msg.BotDbId);
			if (botModel == null)
			{
				await ReleaseLockAsync(msg.ConvoId);
				return;
			}

			var bot = await _db.BlueSkySettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive)
			{
				return;
			}

			try
			{
				// 1. Получаем историю сообщений диалога
				var messages = await _bskyService.GetMessagesAsync(botModel, msg.ConvoId, 10);
				if (messages == null || !messages.Any())
				{
					_logger.LogWarning("[BlueSky] Не удалось загрузить сообщения чата {ConvoId} или чат пуст.", msg.ConvoId);
					return;
				}

				var lastMessage = messages.Last();
				if (lastMessage.Sender.Did == botModel.Did)
				{
					_logger.LogInformation("[BlueSky] Чат {ConvoId}: последнее сообщение отправлено нами. Пропускаем.", msg.ConvoId);
					return;
				}

				string clientDid = lastMessage.Sender.Did;

				// ==================================================================================
				// 2. МОМЕНТАЛЬНЫЙ ПУШ В ИНБОКС (ОПЕРАТОР ВИДИТ СООБЩЕНИЕ НА САЙТЕ)
				// ==================================================================================
				var conversation = await _inboxService.GetOrCreateConversationAsync(
					bot.ProfileId,
					(int)NetworkType.BlueSky,
					bot.Id,
					clientDid,
					"bluesky_user",
					"Пользователь BlueSky",
					null);

				// Привязываем ConvoId
				if (conversation.ExternalChatId != msg.ConvoId)
				{
					var convEntity = await _db.InboxConversations.FindAsync(conversation.Id);
					if (convEntity != null)
					{
						convEntity.ExternalChatId = msg.ConvoId;
						await _db.SaveChangesAsync();
					}
				}

				// Сохраняем входящее сообщение клиента
				await _inboxService.SaveIncomingMessageAsync(
					conversation.Id,
					lastMessage.Id,
					lastMessage.Text);

				// ==================================================================================
				// 3. ПРОВЕРКА HUMAN TAKEOVER (РУЧНОЙ ПЕРЕХВАТ ОПЕРАТОРОМ)
				// ==================================================================================
				if (conversation.IsHumanTakeover)
				{
					if (conversation.HumanTakeoverUntil.HasValue && conversation.HumanTakeoverUntil.Value > DateTime.UtcNow)
					{
						_logger.LogInformation("[BlueSky] 👤 Чат {ConvoId} перехвачен оператором до {Time} UTC. Бот молчит.",
							msg.ConvoId, conversation.HumanTakeoverUntil.Value.ToString("HH:mm"));
						return;
					}
				}

				if (!bot.IsDirectEnabled) return;

				// 4. Формируем историю диалога для модели ИИ
				var chatHistory = messages.Select(m => new AiRequest
				{
					Role = m.Sender.Did == botModel.Did ? "model" : "user",
					Text = m.Text
				}).ToList();

				await _console.Log($"Генерация ответа ИИ для чата {msg.ConvoId} (последнее: «{lastMessage.Text}»)...", bot.UserId, bot.Id);

				// 5. Запрос к ИИ
				var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);

				if (!string.IsNullOrWhiteSpace(aiResponse))
				{
					int typingPause = Random.Shared.Next(8, 16);
					_logger.LogInformation("[BlueSky] 💬 Имитация набора сообщения в чат {ConvoId} ({Sec}с)...", msg.ConvoId, typingPause);
					await Task.Delay(TimeSpan.FromSeconds(typingPause));

					// 6. Отправка ответа в ЛС
					var isSent = await _bskyService.SendChatMessageAsync(botModel, msg.ConvoId, aiResponse);

					if (isSent)
					{
						await _bskyService.MarkConvoAsReadAsync(botModel, msg.ConvoId, lastMessage.Id);
						await _console.Log($"✅ Ответили в чат {msg.ConvoId}: «{aiResponse}»", bot.UserId, bot.Id);

						// 🚀 СОХРАНЯЕМ ОТВЕТ БОТА В ИНБОКС ДЛЯ ОПЕРАТОРА:
						await _inboxService.SaveBotReplyAsync(conversation.Id, aiResponse);
					}
					else
					{
						throw new Exception($"[BlueSky] Сбой отправки сообщения в чат {msg.ConvoId}. Включается MassTransit Retry.");
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка обработки ЛС {ConvoId} для @{Handle}", msg.ConvoId, bot.Handle);
				throw;
			}
			finally
			{
				await ReleaseLockAsync(msg.ConvoId);
			}
		}

		private async Task ReleaseLockAsync(string convoId)
		{
			try
			{
				await _redis.KeyDeleteAsync($"lock:bsky_queued:{convoId}");
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "[BlueSky] Не удалось удалить Redis-замок для чата {ConvoId}", convoId);
			}
		}
	}
}