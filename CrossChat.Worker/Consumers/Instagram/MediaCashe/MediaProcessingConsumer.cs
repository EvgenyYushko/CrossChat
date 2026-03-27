using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers.Instagram.MediaCashe;


public class MediaProcessingConsumer : IConsumer<ProcessMediaCommand>
{
	private readonly IInstagramService _instaService;
	private readonly ILogger<MediaProcessingConsumer> _logger;

	public MediaProcessingConsumer(IInstagramService instaService, ILogger<MediaProcessingConsumer> logger)
	{
		_instaService = instaService;
		_logger = logger;
	}

	public async Task Consume(ConsumeContext<ProcessMediaCommand> context)
	{
		var messageId = context.Message.MessageId;
		var mediaUrl = context.Message.Url;

		// 1. Пытаемся найти запись, которую мы создали при получении вебхука
		if (MediaMessageStorage.Storage.TryGetValue(messageId, out var mediaList))
		{
			MediaDataEntry targetMedia;
			lock (mediaList)
			{
				// Находим ту самую "пустышку" по URL (или можно по ID, если есть)
				targetMedia = mediaList.FirstOrDefault(m => m.Url == mediaUrl && !m.IsProcessed);
			}

			if (targetMedia != null)
			{
				// 2. Обработка (скачиваем и отправляем в Gemini)
				await _instaService.ProcessAndCacheMediaAsync(targetMedia, messageId);

				// Внутри ProcessAndCacheMediaAsync ВЫ ДОЛЖНЫ установить targetMedia.IsProcessed = true;
				// и записать результат в targetMedia.AiResult.

				_logger.LogInformation($"[MediaWorker] Обработано: {messageId}");
			}
		}
		else
		{
			_logger.LogWarning($"[MediaWorker] Запись для {messageId} не найдена в кэше!");
		}
	}
}
