using System.Text.Json;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Modules.Instagram.Models;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class InstagramWebhookController : ControllerBase
	{
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly ILogger<InstagramWebhookController> _logger;
		private const string VerifyToken = "test"; // Задайте свой токен

		public InstagramWebhookController(ILogger<InstagramWebhookController> logger, IPublishEndpoint publishEndpoint)
		{
			_publishEndpoint = publishEndpoint;
			_logger = logger;
		}

		[HttpGet("webhook")]
		public IActionResult VerifyWebhook(
			[FromQuery(Name = "hub.mode")] string mode,
			[FromQuery(Name = "hub.verify_token")] string token,
			[FromQuery(Name = "hub.challenge")] string challenge)
		{
			_logger.LogInformation($"Webhook verification: mode={mode}, token={token}");

			// Проверяем токен верификации
			if (mode == "subscribe" && token == VerifyToken)
			{
				_logger.LogInformation("Webhook verified successfully");
				return Ok(challenge);
			}
			else
			{
				_logger.LogWarning("Webhook verification failed");
				return Forbid();
			}
		}

		[HttpPost("webhook")]
		public async Task<IActionResult> ReceiveWebhook()
		{
			try
			{
				using var reader = new StreamReader(Request.Body);
				var body = await reader.ReadToEndAsync();

				//_logger.LogInformation(body);

				// Десериализуем
				var payload = JsonSerializer.Deserialize<InstagramWebhookPayload>(body);

				if (payload?.Entry == null) return Ok();

				foreach (var entry in payload.Entry)
				{
					// 1. Обработка Сообщений (Direct)
					if (entry.Messaging != null)
					{
						foreach (var messaging in entry.Messaging)
						{
							// Это простое сообщение?
							if (messaging.Message != null && !messaging.Message.IsEcho)
							{
								// Проверка на ответ на сторис
								if (messaging.Message.ReplyTo != null)
								{
									_logger.LogInformation($"Логика ответа на сторис {messaging.Message.ReplyTo}");
								}
								// Обычный текст
								else if (!string.IsNullOrEmpty(messaging.Message.Text))
								{
									await _publishEndpoint.Publish(new InstagramMessageReceived
									{
										SenderId = messaging.Sender.Id,
										RecipientId = messaging.Recipient.Id, // владелец аккаунта
										MessageId = messaging.Message.MessageId,
										ReceivedAt = DateTime.UtcNow
									});
								}
								// Картинка/Видео
								else if (messaging.Message.Attachments != null)
								{
									_logger.LogInformation($"Логика Обработка медиа штук:{messaging.Message.Attachments.Count}");
								}
							}
							// Это реакция?
							else if (messaging.Reaction != null)
							{
								_logger.LogInformation($"Пользователь поставил реакцию {messaging.Reaction.Emoji}");
							}
							// Это удаление?
							else if (messaging.Message != null && messaging.Message.IsDeleted)
							{
								_logger.LogInformation("Пользователь удалил сообщение");
							}
						}
					}

					// 2. Обработка Комментариев (Changes)
					if (entry.Changes != null)
					{
						foreach (var change in entry.Changes)
						{
							if (change.Field == "comments")
							{
								_logger.LogInformation($"Новый коммент от {change.Value.From.Username}: {change.Value.Text}");
								// Тут можно тоже кидать в RabbitMQ, но в другую очередь, например
							}
						}
					}
				}

				return Ok();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error processing Instagram webhook");
				return StatusCode(500);
			}
		}
	}
}
