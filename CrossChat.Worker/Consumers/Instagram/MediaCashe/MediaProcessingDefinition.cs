using MassTransit;

namespace CrossChat.Worker.Consumers.Instagram.MediaCashe;

public class MediaProcessingDefinition : ConsumerDefinition<MediaProcessingConsumer>
{
	public MediaProcessingDefinition()
	{
		// Имя очереди
		EndpointName = "media-processing-queue";
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<MediaProcessingConsumer> consumerConfigurator)
	{
		// 1. ОГРАНИЧЕНИЕ ПАРАЛЛЕЛЬНОСТИ
		// Ставь здесь число, которое вытянет твой сервер (например, 2-3 для Free/Starter).
		// Это значит, что одновременно в памяти будет только 2-3 видео.
		endpointConfigurator.UseConcurrencyLimit(3);

		// 2. RETRY (Повторы)
		// Если произошла ошибка при скачивании или API Gemini глюканул - повторим 3 раза.
		endpointConfigurator.UseMessageRetry(r => r.Interval(3, 2000));
	}
}
