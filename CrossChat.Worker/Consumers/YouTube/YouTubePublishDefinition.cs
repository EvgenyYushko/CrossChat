using MassTransit;

namespace CrossChat.Worker.Consumers.YouTube;

public class YouTubePublishDefinition : ConsumerDefinition<YouTubePublishConsumer>
{
	public YouTubePublishDefinition()
	{
		EndpointName = "youtube-publish-queue";
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<YouTubePublishConsumer> consumerConfigurator)
	{
		// Строго 1 публикация за раз
		endpointConfigurator.UseConcurrencyLimit(1);
		endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
	}
}