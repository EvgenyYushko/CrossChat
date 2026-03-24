using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Telegram.Bot.Types;
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
		// 2. Распознавание
		var mediaEntry = new MediaDataEntry { Url = context.Message.Url, MediaType = context.Message.MediaType };

		// 3. Сохраняем в твой кэш
		MediaMessageStorage.Storage.TryAdd(context.Message.MessageId, new List<MediaDataEntry> { mediaEntry });

		// 1. ПРИНУДИТЕЛЬНАЯ ЗАДЕРЖКА (Throttling)
		// Чтобы не долбить Gemini чаще, чем разрешено (даже если 100 фото пришло)
		await Task.Delay(5000); // Например, 5 секунд между обработками одного файла

		// Используем твой готовый метод
		string result = await _instaService.ProcessAndCacheMediaAsync(mediaEntry, context.Message.MessageId);

		_logger.LogInformation($"[MediaWorker] Обработано: {context.Message.MessageId}");
	}
}
