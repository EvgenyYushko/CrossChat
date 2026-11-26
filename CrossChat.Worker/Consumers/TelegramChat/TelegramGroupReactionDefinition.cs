using MassTransit;

namespace CrossChat.Worker.Consumers.TelegramChat;

public class TelegramGroupReactionDefinition : ConsumerDefinition<TelegramGroupReactionConsumer>
{
	public TelegramGroupReactionDefinition()
	{
		EndpointName = "telegram-group-reaction-queue";
	}

	protected override void ConfigureConsumer(
		IReceiveEndpointConfigurator endpointConfigurator,
		IConsumerConfigurator<TelegramGroupReactionConsumer> consumerConfigurator)
	{
		// СТРОГО 1 ПОТОК: следующее сообщение не начнётся, пока текущее не выждет 10 сек и не поставит лайк!
		endpointConfigurator.UseConcurrencyLimit(1);

		// Повтор при временных сетевых сбоях
		endpointConfigurator.UseMessageRetry(r => r.Interval(2, 2000));
	}
}