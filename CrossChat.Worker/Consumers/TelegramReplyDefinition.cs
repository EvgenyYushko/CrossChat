using MassTransit;

namespace CrossChat.Worker.Consumers
{
	public class TelegramReplyDefinition : ConsumerDefinition<TelegramReplyConsumer>
	{
		public TelegramReplyDefinition()
		{
			EndpointName = "tg-reply-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<TelegramReplyConsumer> consumerConfigurator)
		{
			endpointConfigurator.UseConcurrencyLimit(5);
			endpointConfigurator.UseMessageRetry(r => r.Interval(3, 1000));
			endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)));
		}
	}
}
