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
			// СТРОГО ПО ОДНОМУ: ответы на комментарии публикуются последовательно, как человек
			endpointConfigurator.UseConcurrencyLimit(1);
			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
		}
	}
}