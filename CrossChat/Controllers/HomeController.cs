using Microsoft.AspNetCore.Mvc;

namespace CrossChat.Controllers
{
    // Наследуемся от Controller (для поддержки Views), а не ControllerBase
    public class HomeController : Controller 
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        [HttpGet("")]
        [HttpGet("index")]
        public IActionResult Index()
        {
            // Вся логика и HTML теперь в файле Views/Home/Index.cshtml
            return View();
        }
    }
}