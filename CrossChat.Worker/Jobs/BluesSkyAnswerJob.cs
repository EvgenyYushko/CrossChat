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
						var messages = await _bskyService.GetMessagesAsync(botModel, convo.Id, 15);

						if (messages == null || !messages.Any()) continue;

						// Если последнее сообщение от нас — просто читаем и уходим
						if (convo.LastMessage?.Sender.Did == botModel.Did)
						{
							await _bskyService.MarkConvoAsReadAsync(botModel, convo.Id, convo.LastMessage.Id);
							continue;
						}

						var chatHistory = messages.Select(m => new AiRequest
						{
							// Если DID отправителя совпадает с DID нашего бота - роль "model", иначе "user"
							Role = m.Sender.Did == bot.Did ? "model" : "user",
							Text = m.Text ?? "[Сообщение без текста]"
						}).ToList();

						// 5. Запрос к ИИ
						//var aiResponse = await _aiService.GetAnswerAsync(botModel.SystemPrompt, chatHistory, null);
						var aiResponse = "hi";

						if (!string.IsNullOrWhiteSpace(aiResponse))
						{
							// 6. Отправляем ответ
							bool sent = await _bskyService.SendChatMessageAsync(botModel, convo.Id, aiResponse);

							if (sent)
							{
								// 7. Помечаем последнее сообщение как прочитанное
								await _bskyService.MarkConvoAsReadAsync(botModel, convo.Id, messages.Last().Id);
								_logger.LogInformation($"[BlueSky] Ответили @{bot.Handle} в чат {convo.Id}");
							}
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
