using CrossChat.Data;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.Instagram.MediaCashe;


public class MediaProcessingConsumer : IConsumer<ProcessMediaCommand>
{
	private readonly IInstagramService _instaService;
	private readonly ILogger<MediaProcessingConsumer> _logger;
	private readonly AppDbContext _db;

	public MediaProcessingConsumer(IInstagramService instaService, ILogger<MediaProcessingConsumer> logger, AppDbContext db)
	{
		_instaService = instaService;
		_logger = logger;
		_db = db;
	}

	public async Task Consume(ConsumeContext<ProcessMediaCommand> context)
	{
		var msg = context.Message;

		// 1. Ищем "пустышку" в кэше
		if (!MediaMessageStorage.Storage.TryGetValue(msg.MessageId, out var mediaList))
		{
			_logger.LogWarning($"[MediaWorker] Запись для {msg.MessageId} не найдена в кэше!");
			return;
		}

		MediaDataEntry targetMedia;
		lock (mediaList)
		{
			// Находим ту самую "пустышку" по URL
			targetMedia = mediaList.FirstOrDefault(m => m.Url == msg.Url && !m.IsProcessed);
		}

		if (targetMedia == null) return;

		try
		{
			// 2. БЕЗОПАСНЫЙ ЗАПРОС К БД
			var settings = await _db.InstagramSettings
				.FirstOrDefaultAsync(s => s.InstagramBusinessId == msg.RecipientId);

			if (settings != null)
			{
				var customer = await _db.InstagramBotCustomers
					.FirstOrDefaultAsync(c => c.InstagramSettingsId == settings.Id && c.InstagramSenderId == msg.SenderId);

				bool isIgnored = customer?.IsIgnored ?? false;
				bool isAllowedToProcess = msg.MediaType switch
				{
					"image" => settings.ProcessPhotos,
					"video" => settings.ProcessVideos,
					"audio" => settings.ProcessAudios,
					_ => false
				};

				// 3. ЕСЛИ ЗАПРЕЩЕНО ИЛИ ЮЗЕР В БАНЕ
				if (isIgnored || !isAllowedToProcess)
				{
					_logger.LogInformation($"[MediaWorker] Пропуск медиа {msg.MediaType}. IsIgnored: {isIgnored}, Allowed: {isAllowedToProcess}");

					// ЗАКРЫВАЕМ ЭЛЕМЕНТ, чтобы диалог пошел дальше
					targetMedia.AiResult = isIgnored ? "" : $"[Пользователь отправил {msg.MediaType}, но в настройках бота обработка этого типа файлов отключена]";
					targetMedia.IsProcessed = true;
					return;
				}

				// 4. ПРОВЕРКА ЛИМИТОВ (Делаем только если customer != null)
				if (customer != null)
				{
					var today = DateTime.UtcNow.AddDays(-1);
					var recentResponsesCount = await _db.BotResponseLogs
						.CountAsync(log => log.CustomerId == customer.Id && log.RespondedAt >= today);

					if (recentResponsesCount >= 100)
					{
						_logger.LogWarning($"[Limit] Пользователь {msg.SenderId} превысил лимит (100 ответов). Пропускаем медиа.");

						// ИСПРАВЛЕНИЕ БАГА: Обязательно закрываем элемент! Иначе бот зависнет навсегда.
						targetMedia.AiResult = "";
						targetMedia.IsProcessed = true;
						return;
					}
				}
			}
			else
			{
				// Если настроек бота вообще нет в БД (кто-то удалил аккаунт)
				targetMedia.AiResult = "";
				targetMedia.IsProcessed = true;
				return;
			}

			// 5. ЕСЛИ ВСЁ ОК — скачиваем и отправляем в Gemini
			await _instaService.ProcessAndCacheMediaAsync(targetMedia, msg.MessageId);
			_logger.LogInformation($"[MediaWorker] Успешно обработано: {msg.MessageId}");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, $"[MediaWorker] Ошибка при обработке медиа {msg.MessageId}");

			// ВАЖНО: При ошибке тоже "закрываем" медиа
			targetMedia.AiResult = $"[Ошибка при обработке {msg.MediaType}]";
			targetMedia.IsProcessed = true;

			throw; // Прокидываем ошибку дальше, чтобы MassTransit сделал Retry
		}
	}
}
