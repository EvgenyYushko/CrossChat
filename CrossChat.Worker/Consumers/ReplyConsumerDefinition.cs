using MassTransit;

namespace CrossChat.Worker.Consumers;

public class ReplyConsumerDefinition : ConsumerDefinition<ReplyConsumer>
{
	public ReplyConsumerDefinition()
	{
		// Настройка имени очереди (необязательно, но полезно для порядка)
		EndpointName = "reply-queue";

		// Сколько сообщений RabbitMQ отправит воркеру "наперед"
		// Ставь примерно 2x от лимита конкурентности
		ConcurrentMessageLimit = 10;
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<ReplyConsumer> consumerConfigurator)
	{
		endpointConfigurator.UseConcurrencyLimit(10); 

        // 1. Первая линия обороны (Быстрые ошибки)
        // Если моргнула сеть или база - пробуем быстро 3 раза.
        // Если ошибка RateLimit - эти 3 попытки сгорят за 3 секунды, и мы пойдем ниже.
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, 1000));

        // 2. Вторая линия обороны (Умное ожидание - Redelivery)
        // Если быстрые попытки не помогли, мы ОТКЛАДЫВАЕМ сообщение.
        // r.Intervals(время1, время2, время3...)
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1),  // Попробуй через 1 минуту
            TimeSpan.FromMinutes(2),  // Не вышло? Попробуй через 2 минуты
            TimeSpan.FromMinutes(5)   // Не вышло? Через 5 минут
        ));
	}
}
