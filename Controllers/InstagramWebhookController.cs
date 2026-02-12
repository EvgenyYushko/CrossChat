using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class InstagramWebhookController : ControllerBase
	{
		private readonly ILogger<InstagramWebhookController> _logger;
		private const string VerifyToken = "test"; // Задайте свой токен

		public InstagramWebhookController(ILogger<InstagramWebhookController> logger)
		{
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

				_logger.LogInformation($"Received Instagram webhook: {body}");

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
