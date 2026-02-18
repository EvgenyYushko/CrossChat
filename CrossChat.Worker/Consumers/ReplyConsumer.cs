using System.Threading.RateLimiting;
using CrossChat.Worker.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Consumers;

public class ReplyConsumer : IConsumer<ProcessDialogReply>
{
	private readonly ILogger<ReplyConsumer> _logger;

	// Статический лимитер (один на всё приложение)
	// 2. СОЗДАЕМ ЛИМИТЕР (Static - один на все потоки приложения)
    // Настройка: 15 запросов в 1 минуту (Безопасно для Free Tier)
    private static readonly RateLimiter _rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20,                     // Сколько разрешаем (20 шт)
        Window = TimeSpan.FromMinutes(1),     // За какое время (1 мин)
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0
    });

	// Сюда потом внедришь свои сервисы: IInstagramService, IAiService
	public ReplyConsumer(ILogger<ReplyConsumer> logger)
	{
		_logger = logger;
	}

	public async Task Consume(ConsumeContext<ProcessDialogReply> context)
	{
		// Пытаемся получить разрешение на выполнение
		using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, context.CancellationToken);

		if (lease.IsAcquired)
		{
			// УРА! Нам разрешено. Выполняем логику.
			var senderId = context.Message.SenderId;

			_logger.LogInformation($"[Reply] ⏰ 30 секунд прошло для {senderId}. Начинаем формировать ответ!");

			// 1. Получить историю переписки через API Инстаграма
			// var history = await _instaService.GetHistory(senderId);

			// 2. Отправить в AI
			// var answer = await _aiService.GetAnswer(history);

			// 3. Отправить ответ пользователю
			// await _instaService.SendMessage(senderId, answer);
		}
		else
		{
			// Лимит исчерпан. 
			// Говорим RabbitMQ: "Я не смог, верни сообщение в очередь, попробую позже".
			throw new Exception("Rate limit exceeded (Gemini). Throttling...");

			// Благодаря этому исключению RabbitMQ сделает Redelivery через пару секунд,
			// когда окно лимита освободится.
		}

		await Task.CompletedTask;
	}
}