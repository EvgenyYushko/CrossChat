using MassTransit;

namespace CrossChat.Worker.Consumers.BlueSky
{
	public class BlueSkyCommentDefinition : ConsumerDefinition<BlueSkyCommentConsumer>
	{
		public BlueSkyCommentDefinition()
		{
			EndpointName = "bsky-comment-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<BlueSkyCommentConsumer> consumerConfigurator)
		{
			endpointConfigurator.UseConcurrencyLimit(2);
			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
		}
	}
}