using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace CrossChat.Worker.Jobs
{
	public class BluesSkyAnswerJob : IJob
	{
		private readonly AppDbContext _db;
		private readonly ILogger<BluesSkyAnswerJob> _logger;
		private readonly IBlueSkyService _bskyService;

		public BluesSkyAnswerJob(AppDbContext db
			, ILogger<BluesSkyAnswerJob> logger
			, IBlueSkyService blueSkyService)
		{
			_db = db;
			_logger = logger;
			_bskyService = blueSkyService;
		}

		public async Task Execute(IJobExecutionContext context)
		{
			var activeBots = await _db.BlueSkySettings
				.Where(s => s.IsActive && s.AccessToken != null)
				.ToListAsync();

			foreach (var bot in activeBots)
			{
				_logger.LogInformation($"[BluesJob] Проверка аккаунта @{bot.Handle}");

				try
				{
					var botModel = new BlueSkyModel()
					{
						AccessToken = bot.AccessToken,
						RefreshToken = bot.RefreshToken,
						Handle = bot.Handle,
						PrivateKeyJson = bot.PrivateKeyJson,
						TokenExpiresAt = bot.TokenExpiresAt,
						Did = bot.Did,
						PdsUrl = bot.PdsUrl
					};

					var token = "";
					if (botModel.TokenExpiresAt.HasValue && botModel.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(2))
					{
						token = botModel.AccessToken!;
					}

					_logger.LogInformation($"[BlueSky] Токен для @{botModel.Handle} истек. Обновляем...");

					if (string.IsNullOrEmpty(token))
					{
						var result = await _bskyService.RefreshTokenAsync(botModel.RefreshToken!, botModel.PrivateKeyJson!);

						if (result == null)
						{
							throw new Exception();
						}

						// 3. ОБЯЗАТЕЛЬНО обновляем объект в памяти
						bot.AccessToken = result.Value.AccessToken;
						bot.RefreshToken = result.Value.RefreshToken;
						bot.TokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Value.ExpiresIn);

						// 4. Сохраняем в БД (нужно будет вызвать _db.SaveChangesAsync() в вызывающем коде)
						// Но лучше передать сюда callback или сделать метод сохранения
						_logger.LogInformation($"[BlueSky] Токен успешно обновлен. Новый срок: {bot.TokenExpiresAt}");

						token = bot.AccessToken;
					}

					if (_db.Entry(bot).State == EntityState.Modified)
						await _db.SaveChangesAsync();

					botModel.AccessToken = bot.AccessToken;
					botModel.RefreshToken = bot.RefreshToken;
					botModel.TokenExpiresAt = bot.TokenExpiresAt;

					// 3. Получаем непрочитанные диалоги
					var unreadConvos = await _bskyService.GetUnreadConversationsAsync(botModel);

					foreach (var convo in unreadConvos)
					{
						// Если последнее сообщение от нас — просто читаем и уходим
						if (convo.LastMessage?.Sender.Did == botModel.Did)
						{
							await _bskyService.MarkConvoAsReadAsync(botModel, convo.Id, convo.LastMessage.Id);
							continue;
						}

						// 4. Формируем историю для ИИ
						// Для простоты берем только последнее сообщение, 
						// но можно дописать метод GetMessagesAsync для полноценного контекста.
						var chatHistory = new List<AiRequest> {
							new AiRequest { Role = "user", Text = convo.LastMessage?.Text ?? "" }
						};

						// 5. Запрос к ИИ
						//var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);
						var aiResponse = "hi";

						// 6. Отправка и отметка о прочтении
						bool sent = await _bskyService.SendChatMessageAsync(botModel, convo.Id, aiResponse);
						if (sent && convo.LastMessage != null)
						{
							await _bskyService.MarkConvoAsReadAsync(botModel, convo.Id, convo.LastMessage.Id);
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, $"Ошибка обработки бота {bot.Handle}");
				}
			}
		}
	}
}
