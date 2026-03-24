using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using static CrossChat.Worker.Consumers.Instagram.ReplyConsumer;

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
		await Task.Delay(2000); 

		var messageId = context.Message.MessageId;
		var newMedia = new MediaDataEntry { Url = context.Message.Url, MediaType = context.Message.MediaType };

		// 1. ПОТОКОБЕЗОПАСНОЕ добавление в кэш
		// GetOrAdd позволяет нам либо получить существующий список, либо создать новый
		var mediaList = MediaMessageStorage.Storage.GetOrAdd(messageId, _ => new List<MediaDataEntry>());

		lock (mediaList) // Блокируем список, чтобы не было конфликтов при одновременном добавлении
		{
			mediaList.Add(newMedia);
		}

		// 2. Обработка (только этого конкретного медиа)
		await _instaService.ProcessAndCacheMediaAsync(newMedia, messageId);

		_logger.LogInformation($"[MediaWorker] Обработано: {context.Message.MessageId}");
	}
}
