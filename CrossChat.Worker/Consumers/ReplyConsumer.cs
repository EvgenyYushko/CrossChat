using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers;

public class ReplyConsumer : IConsumer<ProcessDialogReply>
{
    private readonly ILogger<ReplyConsumer> _logger;

    // Сюда потом внедришь свои сервисы: IInstagramService, IAiService
    public ReplyConsumer(ILogger<ReplyConsumer> logger)
    {
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ProcessDialogReply> context)
    {
        var senderId = context.Message.SenderId;

        _logger.LogInformation($"[Reply] ⏰ 30 секунд прошло для {senderId}. Начинаем формировать ответ!");

        // 1. Получить историю переписки через API Инстаграма
        // var history = await _instaService.GetHistory(senderId);

        // 2. Отправить в AI
        // var answer = await _aiService.GetAnswer(history);

        // 3. Отправить ответ пользователю
        // await _instaService.SendMessage(senderId, answer);

        await Task.CompletedTask;
    }
}