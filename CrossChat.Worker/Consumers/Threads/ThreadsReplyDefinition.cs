using MassTransit;

namespace CrossChat.Worker.Consumers.Threads;
public class ThreadsReplyDefinition : ConsumerDefinition<ThreadsReplyConsumer>
{
	public ThreadsReplyDefinition()
	{
		// Явно задаем имя очереди в RabbitMQ
		EndpointName = "threads-reply-queue";
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<ThreadsReplyConsumer> consumerConfigurator)
	{
		// 1. Ограничение параллельности (Concurrency Limit)
		// Threads API довольно строгий к частоте запросов, поэтому 
		// 5 одновременных потоков — оптимально для начала.
		endpointConfigurator.UseConcurrencyLimit(5);

		// 2. Быстрые повторы (Retry)
		// Если API Threads или твой gRPC сервис ИИ кратковременно недоступен.
		// 3 попытки с интервалом в 2 секунды.
		endpointConfigurator.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));

		// 3. Отложенные повторы (Redelivery)
		// Если Meta вернула ошибку лимитов (Rate Limit) или ИИ заблокировал контент.
		// Сообщение будет отложено и вернется в воркер через минуту.
		endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
			TimeSpan.FromMinutes(1),
			TimeSpan.FromMinutes(2),
			TimeSpan.FromMinutes(5)
		));
	}
}
