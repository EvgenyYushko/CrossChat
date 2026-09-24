using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
			var processingKey = $"processed:fb:{msg.ReplyId}";

			// Защита от дублей
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

				// 2. Скачиваем ВСЮ историю переписки (со всеми сообщениями за последние 30 секунд!)
				var messages = await _faceBookService.GetMessagesBySenderIdAsync(bot.PageId, msg.SenderId, bot.PageAccessToken, 10);
				if (messages == null || !messages.Any()) return;

				// Если последнее сообщение в чате отправлено самой страницей — мы уже ответили, выходим
				if (messages.First().FromId == bot.PageId)
				{
					await _redis.StringSetAsync(processingKey, "done", TimeSpan.FromMinutes(10), When.NotExists);
					return;
				}

				// 3. Формируем единый контекст всех полученных сообщений
				// Разворачиваем от старых к новым
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

				await _console.Log($"Генерация ответа в ЛС для {msg.SenderId} (Режим: {mode}, накопилось сообщений: {messages.Count(m => m.FromId != bot.PageId)})", bot.UserId, bot.Id);

				// === СЦЕНАРИЙ 2: ТОЛЬКО ШАБЛОНЫ (Spintax) ===
				if (mode == 2)
				{
					replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
				}
				// === СЦЕНАРИЙ 1: ТОЛЬКО ИИ ===
				else if (mode == 1)
				{
					replyText = await _aiService.GetAnswerAsync(bot.SystemPrompt, chatHistory, null);
				}
				// === СЦЕНАРИЙ 3: КОМБИНИРОВАННЫЙ ===
				else if (mode == 3)
				{
					try
					{
						replyText = await _aiService.GetAnswerAsync(bot.SystemPrompt, chatHistory, null);
					}
					catch (Exception aiEx)
					{
						_logger.LogWarning(aiEx, "[Facebook Direct] Сбой ИИ для ЛС. Переход на шаблон.");
					}

					if (string.IsNullOrWhiteSpace(replyText))
					{
						replyText = InstagramCommentEngine.GetRandomTemplate(bot.DirectTemplates);
					}
				}

				if (string.IsNullOrWhiteSpace(replyText)) return;

				// Имитация печати: показываем статус "печатает..." 2-3 секунды
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
			finally
			{
				// Удаляем ключ дебаунса
				await _redis.KeyDeleteAsync($"debounce:fb:{msg.SenderId}:{msg.PageId}");
			}
		}
	}
}