using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
    [Route("privacy")]
    public class PrivacyController : Controller
    {
        [HttpGet("")]
        [HttpGet("policy")]
        public IActionResult Index()
        {
            return View(); // Ищет Views/Privacy/Index.cshtml
        }
    }
}