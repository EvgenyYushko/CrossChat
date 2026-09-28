using MassTransit;

namespace CrossChat.Worker.Consumers.Threads
{
	public class ThreadsPublishDefinition : ConsumerDefinition<ThreadsPublishConsumer>
	{
		public ThreadsPublishDefinition()
		{
			EndpointName = "threads-publish-queue";
		}

		protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<ThreadsPublishConsumer> consumerConfigurator)
		{
			// СТРОГО ПО ОДНОМУ: публикации уходят по очереди, исключая одновременные выстрелы
			endpointConfigurator.UseConcurrencyLimit(1);

			endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
		}
	}
}