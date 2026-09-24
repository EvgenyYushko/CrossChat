using MassTransit;

namespace CrossChat.Worker.Consumers.Facebook.Comments
{
	public class FacebookCommentDefinition : ConsumerDefinition<FacebookCommentConsumer>
	{
		public FacebookCommentDefinition()
		{
			EndpointName = "fb-comment-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<FacebookCommentConsumer> consumerConfigurator)
		{
			endpointConfigurator.UseConcurrencyLimit(3);
			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
		}
	}
}