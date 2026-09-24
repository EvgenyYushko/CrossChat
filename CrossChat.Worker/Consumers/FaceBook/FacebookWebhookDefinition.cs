using MassTransit;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FacebookWebhookDefinition : ConsumerDefinition<FacebookWebhookConsumer>
	{
		public FacebookWebhookDefinition()
		{
			EndpointName = "fb-webhook-debounce-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<FacebookWebhookConsumer> consumerConfigurator)
		{
			endpointConfigurator.UseConcurrencyLimit(10);
			endpointConfigurator.UseMessageRetry(r => r.Interval(2, 500));
		}
	}
}