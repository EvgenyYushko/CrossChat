using MassTransit;

namespace CrossChat.Worker.Consumers.FaceBook
{
	public class FaceBookReplyDefinition : ConsumerDefinition<FaceBookReplyConsumer>
	{
		public FaceBookReplyDefinition()
		{
			EndpointName = "fsbk-reply-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<FaceBookReplyConsumer> consumerConfigurator)
		{
			endpointConfigurator.UseConcurrencyLimit(2);

			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
			endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
		}
	}
}
