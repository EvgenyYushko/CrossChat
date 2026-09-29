using MassTransit;

namespace CrossChat.Worker.Consumers.YouTube;

public class YouTubeReplyDefinition : ConsumerDefinition<YouTubeReplyConsumer>
{
	public YouTubeReplyDefinition()
	{
		EndpointName = "youtube-reply-queue";
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<YouTubeReplyConsumer> consumerConfigurator)
	{
		// Обработка строго по одному
		endpointConfigurator.UseConcurrencyLimit(1);
		endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
	}
}