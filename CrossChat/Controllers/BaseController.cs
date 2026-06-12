using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
	public abstract class BaseController : Controller
	{
		protected int? GetActiveProfileId()
		{
			return HttpContext.Session.GetInt32("ActiveProfileId");
		}

		protected void SetActiveProfileId(int profileId)
		{
			HttpContext.Session.SetInt32("ActiveProfileId", profileId);
		}
	}
}
