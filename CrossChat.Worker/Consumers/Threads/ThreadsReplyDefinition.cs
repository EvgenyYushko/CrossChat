using MassTransit;

namespace CrossChat.Worker.Consumers.Threads;

public class ThreadsReplyDefinition : ConsumerDefinition<ThreadsReplyConsumer>
{
	public ThreadsReplyDefinition()
	{
		EndpointName = "threads-reply-queue";
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<ThreadsReplyConsumer> consumerConfigurator)
	{
		// Строго по одному!
		endpointConfigurator.UseConcurrencyLimit(1);

		// Если что-то упало - пробуем еще раз
		endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

		// Если долго не получается - откладываем на 5 минут
		endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(5)));
	}
}
