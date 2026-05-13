using MassTransit;

namespace CrossChat.Worker.Consumers.Instagram;

public class ReplyConsumerDefinition : ConsumerDefinition<ReplyConsumer>
{
	public ReplyConsumerDefinition()
	{
		// Настройка имени очереди (необязательно, но полезно для порядка)
		EndpointName = "reply-queue";

		// Сколько сообщений RabbitMQ отправит воркеру "наперед"
		// Ставь примерно 2x от лимита конкурентности

		// Ваш лимитер = 20 запросов/минуту
        // Значит максимальная пропускная способность = 20/60 ≈ 0.33 запроса/секунду
        // ConcurrentMessageLimit = 10 избыточен, т.к. лимитер все равно ограничит
		ConcurrentMessageLimit = 5;
	}

	protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<ReplyConsumer> consumerConfigurator)
	{
		endpointConfigurator.UseConcurrencyLimit(10);  // Максимум 10 сообщений обрабатываются параллельно

		// 1. Первая линия обороны (Быстрые ошибки)
		// Если моргнула сеть или база - пробуем быстро 3 раза.
		// Если ошибка RateLimit - эти 2 попытки сгорят за 1 секунды, и мы пойдем ниже.
		endpointConfigurator.UseMessageRetry(r => r.Interval(2, 500));

		// 2. Вторая линия обороны (Умное ожидание - Redelivery)
		// Если быстрые попытки не помогли, мы ОТКЛАДЫВАЕМ сообщение.
		// r.Intervals(время1, время2, время3...)
		endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
			TimeSpan.FromSeconds(30),  // 30 секунд (вдруг окно освободилось)
            TimeSpan.FromSeconds(60),  // 1 минута (гарантированно новое окно)
            TimeSpan.FromMinutes(3),   // 3 минуты
            TimeSpan.FromMinutes(5)    // 5 минут
		));

		// RabbitMQ (благодаря Definition):
		// 1-я попытка: Rate Limit превышен -> throw -> Interval(1 sec)
		// 2-я попытка: снова throw -> Interval(1 sec)  
		// 3-я попытка: снова throw -> Interval(1 sec)
		// 4-я попытка: DelayedRedelivery через 1 минуту (окно лимитера обновилось!)
		// 5-я попытка: через 2 минуты
		// 6-я попытка: через 5 минут
	}
}
