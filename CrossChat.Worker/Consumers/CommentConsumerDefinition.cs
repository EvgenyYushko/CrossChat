using MassTransit;

namespace CrossChat.Worker.Consumers;

public class CommentConsumerDefinition : ConsumerDefinition<CommentConsumer>
{
    public CommentConsumerDefinition()
    {
        EndpointName = "comment-queue";
    }

    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<CommentConsumer> consumerConfigurator)
    {
        // Не более 5 параллельных потоков для комментов
        endpointConfigurator.UseConcurrencyLimit(5);
        
        // Быстрые ретраи при сетевых ошибках
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, 1000));
        
        // Отложенный повтор (Redelivery), если уперлись в Rate Limit
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1), 
            TimeSpan.FromMinutes(2)
        ));
    }
}