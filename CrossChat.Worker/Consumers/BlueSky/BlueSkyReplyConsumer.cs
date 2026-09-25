using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Services;
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

		public BlueSkyReplyConsumer(
			AppDbContext db,
			IBlueSkyService bskyService,
			IAiService aiService,
			IConnectionMultiplexer redis,
			IBlueSkyConsole console,
			ILogger<BlueSkyReplyConsumer> logger)
		{
			_db = db;
			_bskyService = bskyService;
			_aiService = aiService;
			_redis = redis.GetDatabase();
			_console = console;
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<BlueSkyProcessReply> context)
		{
			var msg = context.Message;

			// 1. Достаем настройки бота из БД
			var bot = await _db.BlueSkySettings.FindAsync(msg.BotDbId);
			if (bot == null || !bot.IsActive || !bot.IsDirectEnabled)
			{
				// Если бот отключен — сразу освобождаем замок и выходим
				await ReleaseLockAsync(msg.ConvoId);
				return;
			}

			try
			{
				var botModel = new BlueSkyModel
				{
					AccessToken = bot.AccessToken!,
					RefreshToken = bot.RefreshToken,
					Handle = bot.Handle,
					PrivateKeyJson = bot.PrivateKeyJson!,
					TokenExpiresAt = bot.TokenExpiresAt,
					Did = bot.Did!,
					PdsUrl = bot.PdsUrl!,
					SystemPrompt = bot.SystemPrompt
				};

				// 2. ВАЖНО: Гарантируем, что токен свежий перед походом в API
				await _bskyService.GetValidTokenAsync(botModel);
				if (bot.AccessToken != botModel.AccessToken)
				{
					bot.AccessToken = botModel.AccessToken;
					bot.RefreshToken = botModel.RefreshToken;
					bot.TokenExpiresAt = botModel.TokenExpiresAt;
					await _db.SaveChangesAsync();
				}

				// 3. Получаем историю сообщений диалога
				var messages = await _bskyService.GetMessagesAsync(botModel, msg.ConvoId, 10);
				if (messages == null || !messages.Any())
				{
					_logger.LogWarning("[BlueSky] Не удалось загрузить сообщения чата {ConvoId} или чат пуст.", msg.ConvoId);
					return;
				}

				// 4. Двойная проверка: если последнее сообщение отправлено нами — ответ не требуется
				var lastMessage = messages.Last();
				if (lastMessage.Sender.Did == botModel.Did)
				{
					_logger.LogInformation("[BlueSky] Чат {ConvoId}: последнее сообщение отправлено нами. Пропускаем.", msg.ConvoId);
					return;
				}

				// 5. Формируем историю диалога для модели ИИ
				var chatHistory = messages.Select(m => new AiRequest
				{
					Role = m.Sender.Did == botModel.Did ? "model" : "user",
					Text = m.Text
				}).ToList();

				await _console.Log($"Генерация ответа ИИ для чата {msg.ConvoId} (последнее: «{lastMessage.Text}»)...", bot.UserId, bot.Id);

				// 6. Запрос к ИИ
				var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);

				if (!string.IsNullOrWhiteSpace(aiResponse))
				{
					// 7. Отправка ответа в ЛС
					var isSent = await _bskyService.SendChatMessageAsync(botModel, msg.ConvoId, aiResponse);

					if (isSent)
					{
						// 8. Помечаем диалог прочитанным в BlueSky
						await _bskyService.MarkConvoAsReadAsync(botModel, msg.ConvoId, lastMessage.Id);
						await _console.Log($"✅ Ответили в чат {msg.ConvoId}: «{aiResponse}»", bot.UserId, bot.Id);
					}
					else
					{
						// ВАЖНО ДЛЯ РЕТРАЯ: Бросаем исключение, чтобы MassTransit попробовал повторить отправку!
						throw new Exception($"[BlueSky] Сбой отправки сообщения в чат {msg.ConvoId}. Включается MassTransit Retry.");
					}
				}
				else
				{
					_logger.LogWarning("[BlueSky] ИИ вернул пустой ответ для чата {ConvoId}", msg.ConvoId);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[BlueSky] Ошибка обработки ЛС {ConvoId} для @{Handle}", msg.ConvoId, bot.Handle);
				// Пробрасываем ошибку дальше в MassTransit, чтобы сработал Retry
				throw;
			}
			finally
			{
				// Удаляем Redis-замок, чтобы этот чат мог снова встать в очередь при новых сообщениях
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