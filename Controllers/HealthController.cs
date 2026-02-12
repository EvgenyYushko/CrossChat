using Microsoft.AspNetCore.Mvc;
using static CrossChat.Helpers.TimeZoneHelper;

namespace CrossChat.Controllers
{
	[ApiExplorerSettings(IgnoreApi = true)]
	[ApiController]
	[Route("[controller]")]
	public class HealthController : ControllerBase
	{
		[HttpGet("/health")]
		[HttpHead("/health")]
		public IActionResult GetHealthStatus()
		{
			return Ok($"App is running {DateTimeNow}");
		}
	}
}
