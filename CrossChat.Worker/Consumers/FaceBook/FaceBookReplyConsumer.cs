using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FaceBookReplyConsumer : IConsumer<ProcessFacebookDialogReply>
	{
		private readonly AppDbContext _db;
		private readonly IFaceBookService _faceBookService;
		private readonly IAiService _aiService;
		private readonly IFaceBookConsole _console;
		private readonly IDatabase _redis;
		private readonly ILogger<FaceBookReplyConsumer> _logger;

		public FaceBookReplyConsumer(
			AppDbContext db,
			IFaceBookService faceBookService,
			IAiService aiService,
			IConnectionMultiplexer redis,
			IFaceBookConsole console,
			ILogger<FaceBookReplyConsumer> logger)
		{
			_db = db;
			_faceBookService = faceBookService;
			_aiService = aiService;
			_console = console;
			_redis = redis.GetDatabase();
			_logger = logger;
		}

		public async Task Consume(ConsumeContext<ProcessFacebookDialogReply> context)
		{
			var msg = context.Message;
			var targetTimeKey = $"debounce:target_time:fb:{msg.SenderId}:{msg.PageId}";
			var activeTimerKey = $"debounce:timer_active:fb:{msg.SenderId}:{msg.PageId}";

			// === ПРОВЕРКА СКОЛЬЗЯЩЕГО ТАЙМЕРА (НАСТОЯЩИЙ DEBOUNCE) ===
			var storedTicks = await _redis.StringGetAsync(targetTimeKey);
			if (storedTicks.HasValue && long.TryParse(storedTicks, out long ticks))
			{
				var targetTimeUtc = new DateTime(ticks, DateTimeKind.Utc);
				var remaining = targetTimeUtc - DateTime.UtcNow;

				// Если пользователь продолжал писать и до нового срока осталось более 2 секунд:
				if (remaining > TimeSpan.FromSeconds(2))
				{
					_logger.LogInformation("[Facebook Debounce] ⏳ Пользователь {Sender} всё еще пишет! Откладываем ответ еще на {Sec:F0} сек...",
						msg.SenderId, remaining.TotalSeconds);

					// Переназначаем себя в очередь на оставшееся время и ВЫХОДИМ без отправки!
					await context.SchedulePublish(remaining, context.Message);
					return;
				}
			}

			// Время вышло, наступила полная тишина — очищаем ключи таймера
			await _redis.KeyDeleteAsync(targetTimeKey);
			await _redis.KeyDeleteAsync(activeTimerKey);

			var processingKey = $"processed:fb:{msg.ReplyId}";
			if (await _redis.KeyExistsAsync(processingKey))
			{
				_logger.LogInformation("[Facebook Reply] Сообщение уже обработано. Пропуск.");
				return;
			}

			try
			{
				// 1. Ищем страницу в БД
				var bot = await _db.FacebookSettings
					.AsNoTracking()
					.FirstOrDefaultAsync(s => s.PageId == msg.PageId);

				if (bot == null || !bot.IsActive || !bot.IsDirectEnabled || string.IsNullOrEmpty(bot.PageAccessToken))
					return;

				// 2. Скачиваем ВСЮ пачку сообщений, накопившихся за всё время печати
				var messages = await _faceBookService.GetMessagesBySenderIdAsync(bot.PageId, msg.SenderId, bot.PageAccessToken, 15);
				if (messages == null || !messages.Any()) return;

				// Если последнее сообщение в чате отправлено самой страницей — выходим
				if (messages.First().FromId == bot.PageId)
				{
					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(10), When.NotExists);
					return;
				}

				// 3. Формируем контекст истории
				var chatHistory = new List<AiRequest>();
				for (int i = messages.Count - 1; i >= 0; i--)
				{
					var m = messages[i];
					string role = m.FromId == bot.PageId ? "model" : "user";
					chatHistory.Add(new AiRequest
					{
						Role = role,
						Text = m.Text
					});
				}

				int mode = bot.DirectReplyMode > 0 ? bot.DirectReplyMode : 2;
				string? replyText = null;

				int totalUserMsgs = messages.Count(m => m.FromId != bot.PageId);
				await _console.Log($"Формирование одного ответа на ВСЮ серию из {totalUserMsgs} сообщений для {msg.SenderId} (Режим: {mode})", bot.UserId, bot.Id);

				// Сценарии генерации:
				if (mode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
				}
				else if (mode == 1)
				{
					replyText = await _aiService.GetAnswerAsync(bot.SystemPrompt, chatHistory, null);
				}
				else if (mode == 3)
				{
					try
					{
						replyText = await _aiService.GetAnswerAsync(bot.SystemPrompt, chatHistory, null);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[Facebook Direct] Сбой ИИ, переключение на шаблон.");
					}

					if (string.IsNullOrWhiteSpace(replyText))
					{
						replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText)) return;

				// Имитация печати человека
				await _faceBookService.SetTypingStatusAsync(msg.SenderId, bot.PageAccessToken);
				await Task.Delay(2500);

				// Отправляем ОДИН ответ на всю серию сообщений!
				bool sent = await _faceBookService.SendReplyAsync(msg.SenderId, replyText, bot.PageAccessToken);
				if (sent)
				{
					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(10), When.NotExists);
					await _console.Log($"✅ Ответили на серию сообщений в ЛС {msg.SenderId}: «{replyText}»", bot.UserId, bot.Id);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "[Facebook Reply] Ошибка при формировании ответа в ЛС {SenderId}", msg.SenderId);
			}
		}
	}
}