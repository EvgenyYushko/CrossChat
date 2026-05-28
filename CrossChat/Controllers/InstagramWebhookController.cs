using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Consumers.Instagram;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Modules.Instagram.Models;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class InstagramWebhookController : ControllerBase
	{
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IUserConsoleService _consoleService;
		private readonly ILogger<InstagramWebhookController> _logger;
		private const string VerifyToken = "test"; // Задайте свой токен

		public InstagramWebhookController(ILogger<InstagramWebhookController> logger, IPublishEndpoint publishEndpoint, IUserConsoleService consoleService)
		{
			_publishEndpoint = publishEndpoint;
			_consoleService = consoleService;
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

				_logger.LogInformation(body);

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
								if (messaging.Message.ReplyTo != null && !messaging.Message.ReplyTo.IsSelfReply)
								{
									_logger.LogInformation($"Логика ответа на реплей");
									await PublishMessage(messaging);
								}
								// Обычный текст
								else if (!string.IsNullOrEmpty(messaging.Message.Text) || messaging.Message.IsUnsupported)
								{
									await PublishMessage(messaging);
								}
								// Картинка/Видео
								else if (messaging.Message.Attachments?.Count > 0 && !messaging.Message.Attachments.Any(t => t.Type == "template"))
								{
									var messageId = messaging.Message.MessageId;
									var mediaList = MediaMessageStorage.Storage.GetOrAdd(messageId, _ => new List<MediaDataEntry>());
									var InstagramBusinessId = entry.Id;

									foreach (var attach in messaging.Message.Attachments)
									{
										var type = attach.Type;
										var url = attach.Payload?.Url;
										if (string.IsNullOrEmpty(url)) continue;

										var emptyMedia = new MediaDataEntry
										{
											Url = url,
											MediaType = type,
											IsProcessed = false // ОЧЕНЬ ВАЖНО
										};

										lock (mediaList)
										{
											mediaList.Add(emptyMedia);
										}

										// КИДАЕМ В ОЧЕРЕДЬ
										await _publishEndpoint.Publish(new ProcessMediaCommand
										{
											MessageId = messageId,
											Url = url,
											MediaType = type,
											SenderId = messaging.Sender.Id,        // Передаем ID отправителя
											RecipientId = messaging.Recipient.Id   // Передаем ID бизнес-аккаунта
										});
									}

									await PublishMessage(messaging);
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
								var InstagramBusinessId = entry.Id;
								var value = change.Value; // Это InstagramChangeValue (из твоей модели)

								// Защита: не отвечаем самим себе (если бот написал коммент, не надо на него отвечать)
								// (В идеале нужно проверить, не совпадает ли value.From.Id с entry.Id)
								_logger.LogInformation($"[Webhook] Новый коммент от {value.From.Username}: {value.Text}");

								if (value.From?.Id == InstagramBusinessId)
								{
									_logger.LogInformation($"Ignoring comment from self (bot)");
									return Ok();
								}

								if (value.ParentId is not null)
								{
									_logger.LogInformation($"Ignoring comment from Parent");
									return Ok();
								}

								// Отправляем в RabbitMQ!
								await _publishEndpoint.Publish(new InstagramCommentReceived
								{
									BusinessAccountId = InstagramBusinessId, // ID страницы, куда прилетел коммент
									CommentId = value.Id,
									Text = value.Text,
									Username = value.From?.Username ?? "user",
									// Если в модели есть ParentId, передай его, иначе оставь null
								});
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

		private async Task PublishMessage(InstagramMessaging messaging)
		{
			await _publishEndpoint.Publish(new InstagramMessageReceived
			{
				SenderId = messaging.Sender.Id,
				RecipientId = messaging.Recipient.Id, // владелец аккаунта
				MessageId = messaging.Message.MessageId,
				ReceivedAt = DateTime.UtcNow,
				AttachmentCount = messaging.Message.Attachments?.Count ?? 0
			});
		}
	}
}
