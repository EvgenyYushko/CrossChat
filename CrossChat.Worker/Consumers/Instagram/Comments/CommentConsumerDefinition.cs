using MassTransit;

namespace CrossChat.Worker.Consumers.Instagram.Comments;

public class CommentConsumerDefinition : ConsumerDefinition<CommentConsumer>
{
    public CommentConsumerDefinition()
    {
        EndpointName = "comment-queue";
    }

    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator, IConsumerConfigurator<CommentConsumer> consumerConfigurator)
    {
        // СТРОГО 1 ПОТОК: комментарии уходят по очереди с паузами, как у живого SMM-щика
        endpointConfigurator.UseConcurrencyLimit(1);
        
        // Быстрые ретраи при сетевых ошибках
        endpointConfigurator.UseMessageRetry(r => r.Interval(3, 1000));
        
        // Отложенный повтор при серьезных сбоях
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(
            TimeSpan.FromMinutes(1), 
            TimeSpan.FromMinutes(2)
        ));
    }
}