using System.Text.Json;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Worker.Consumers.Instagram;
using CrossChat.Worker.Contracts;
using CrossChat.Worker.Modules.Instagram.Models;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using static CrossChat.Helpers.TimeZoneHelper;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class InstagramWebhookController : ControllerBase
	{
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IUserConsoleService _consoleService;
		private readonly IInboxService _inboxService; // <-- ВНЕДРЯЕМ ИНБОКС
		private readonly ILogger<InstagramWebhookController> _logger;
		private const string VerifyToken = "test";

		public InstagramWebhookController(
			ILogger<InstagramWebhookController> logger,
			IPublishEndpoint publishEndpoint,
			IUserConsoleService consoleService,
			IInboxService inboxService) // <-- ДОБАВЛЕНО
		{
			_publishEndpoint = publishEndpoint;
			_consoleService = consoleService;
			_inboxService = inboxService;
			_logger = logger;
		}

		[HttpGet("webhook")]
		public IActionResult VerifyWebhook(
			[FromQuery(Name = "hub.mode")] string mode,
			[FromQuery(Name = "hub.verify_token")] string token,
			[FromQuery(Name = "hub.challenge")] string challenge)
		{
			_logger.LogInformation($"Webhook verification: mode={mode}, token={token}");

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

				var payload = JsonSerializer.Deserialize<InstagramWebhookPayload>(body);
				if (payload?.Entry == null) return Ok();

				foreach (var entry in payload.Entry)
				{
					var instagramBusinessId = entry.Id;

					// ===================================================================
					// 1. ОБРАБОТКА ДИАЛОГОВ (DIRECT)
					// ===================================================================
					if (entry.Messaging != null)
					{
						foreach (var messaging in entry.Messaging)
						{
							// ===================================================================
							// 1. РЕАКЦИИ (У НИХ messaging.Message ВСЕГДА NULL!)
							// ===================================================================
							if (messaging.Reaction != null)
							{
								var mid = messaging.Reaction.MessageId; // ID сообщения
								var emoji = messaging.Reaction.Emoji;
								var action = messaging.Reaction.Action; // "react" или "unreact"

								_logger.LogInformation("❤️ [Instagram Reaction Webhook] Действие: {Action}, Emoji: {Emoji}, Mid: {Mid}", action, emoji, mid);

								if (!string.IsNullOrEmpty(mid))
								{
									await _inboxService.HandleReactionWebhookAsync(
										instagramBusinessId,
										mid,
										emoji,
										action);
								}
								continue; // Переходим к следующему объекту
							}

							// Если нет ни сообщения, ни реакции — пропускаем
							if (messaging.Message == null) continue;

							// ===================================================================
							// 2. ИСХОДЯЩИЕ ЭХО-СООБЩЕНИЯ (ОТВЕТ С ТЕЛЕФОНА)
							// ===================================================================
							if (messaging.Message.IsEcho)
							{
								var customerId = messaging.Recipient?.Id;
								var text = messaging.Message.Text;
								var mid = messaging.Message.MessageId;

								if (!string.IsNullOrEmpty(customerId) && !string.IsNullOrEmpty(text))
								{
									int networkTypeInstagram = 1;
									await _inboxService.SaveEchoMessageAsync(
										networkTypeInstagram,
										instagramBusinessId,
										customerId,
										mid,
										text);
								}
								continue;
							}

							// ===================================================================
							// 3. ВХОДЯЩИЕ СООБЩЕНИЯ ОТ КЛИЕНТА
							// ===================================================================
							if (messaging.Message.ReplyTo != null && !messaging.Message.ReplyTo.IsSelfReply)
							{
								_logger.LogInformation("Логика ответа на реплей");
								await PublishMessage(messaging);
							}
							else if (!string.IsNullOrEmpty(messaging.Message.Text) || messaging.Message.IsUnsupported)
							{
								await PublishMessage(messaging);
							}
							else if (messaging.Message.Attachments?.Count > 0 && !messaging.Message.Attachments.Any(t => t.Type == "template"))
							{
								var messageId = messaging.Message.MessageId;
								var mediaList = MediaMessageStorage.Storage.GetOrAdd(messageId, _ => new List<MediaDataEntry>());

								foreach (var attach in messaging.Message.Attachments)
								{
									var type = attach.Type;
									var url = attach.Payload?.Url;
									if (string.IsNullOrEmpty(url)) continue;

									var emptyMedia = new MediaDataEntry
									{
										Url = url,
										MediaType = type,
										IsProcessed = false
									};

									lock (mediaList)
									{
										mediaList.Add(emptyMedia);
									}

									await _publishEndpoint.Publish(new ProcessMediaCommand
									{
										MessageId = messageId,
										Url = url,
										MediaType = type,
										SenderId = messaging.Sender.Id,
										RecipientId = messaging.Recipient.Id
									});
								}

								await PublishMessage(messaging);
							}
							else if (messaging.Message.IsDeleted)
							{
								_logger.LogInformation("Пользователь удалил сообщение");
							}
						}
					}

					// ===================================================================
					// 2. ОБРАБОТКА КОММЕНТАРИЕВ (CHANGES)
					// ===================================================================
					if (entry.Changes != null)
					{
						foreach (var change in entry.Changes)
						{
							if (change.Field == "comments")
							{
								var value = change.Value;
								_logger.LogInformation($"[Webhook] Новый коммент от {value.From?.Username}: {value.Text}");

								if (value.From?.Id == instagramBusinessId) continue;
								if (value.ParentId is not null) continue;

								await _publishEndpoint.Publish(new InstagramCommentReceived
								{
									BusinessAccountId = instagramBusinessId,
									CommentId = value.Id,
									Text = value.Text ?? "",
									Username = value.From?.Username ?? "user",
									MediaId = value.Media?.Id
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
			string? messageText = messaging.Message?.Text;
			if (string.IsNullOrEmpty(messageText) && messaging.Message?.Attachments?.Count > 0)
			{
				messageText = $"[{messaging.Message.Attachments[0].Type}]";
			}

			await _publishEndpoint.Publish(new InstagramMessageReceived
			{
				SenderId = messaging.Sender.Id,
				RecipientId = messaging.Recipient.Id,
				MessageId = messaging.Message.MessageId,
				Text = messageText,
				ReceivedAt = DateTimeNow,
				AttachmentCount = messaging.Message.Attachments?.Count ?? 0
			});
		}
	}
}