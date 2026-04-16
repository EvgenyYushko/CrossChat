using MassTransit;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyReplyDefinition : ConsumerDefinition<BlueSkyReplyConsumer>
	{
		public BlueSkyReplyDefinition()
		{
			EndpointName = "bsky-reply-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<BlueSkyReplyConsumer> consumerConfigurator)
		{
			// Для BlueSky лучше держать лимит 1 или 2, чтобы не конфликтовали Nonce-ы в DPoP
			endpointConfigurator.UseConcurrencyLimit(2);

			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
			endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
		}
	}
}
