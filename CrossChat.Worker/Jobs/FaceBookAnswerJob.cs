using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs
{
	[DisallowConcurrentExecution]
	public class FaceBookAnswerJob : IJob
	{
		private IFaceBookService _fbService;
		private ILogger<FaceBookAnswerJob> _logger;
		private readonly AppDbContext _db;

		public FaceBookAnswerJob(IFaceBookService fbService, ILogger<FaceBookAnswerJob> logger, AppDbContext db)
		{
			_fbService = fbService;
			_logger = logger;
			_db = db;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			try
			{
				var activeBots = await _db.FacebookSettings
					.Where(s => s.IsActive)
					.ToListAsync();

				foreach (var bot in activeBots)
				{
					_logger.LogInformation($"[FaceBookAnswerJob] Проверка аккаунта @{bot.PageName}");

					// 1. Получаем сообщения, на которые нужно ответить
					var incomingMessages = await _fbService.GetUnreadMessagesAsync(bot.PageAccessToken, bot.PageId);

					if (incomingMessages == null || !incomingMessages.Any()) return;

					foreach (var msg in incomingMessages)
					{
						_logger.LogInformation($"Входящее FB сообщение от {msg.from.name}: {msg.message}");

						// 2. Генерируем ответ (Gemini)
						//string prompt = GetPrompt(msg.message);

						string replyText = "Привет)";//await _aiModel.GeminiRequest(prompt);

						await Task.Delay(TimeSpan.FromSeconds(15));
						// 3. Отправляем
						// Важно: msg.from.id - это ID пользователя (Recipient ID)
						await _fbService.SendReplyAsync(msg.from.id, replyText, bot.PageAccessToken);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError($"Ошибка в FaceBookDmJob: {ex.Message}");
			}
		}
	}
}
