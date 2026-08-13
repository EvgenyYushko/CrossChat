using CrossChat.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
    public class HomeController : Controller 
    {
        private readonly AppDbContext _db;
        private readonly ILogger<HomeController> _logger;

        public HomeController(AppDbContext db, ILogger<HomeController> logger)
        {
            _db = db;
            _logger = logger;
        }

        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index()
        {
            // Выбираем до 10 последних отзывов с оценкой 4 или 5 звезд
            var topReviews = await _db.Reviews
                .Include(r => r.User)
                .Where(r => r.Rating >= 4)
                .OrderByDescending(r => r.CreatedAt)
                .Take(10)
                .ToListAsync();

            ViewBag.TopReviews = topReviews;

            return View();
        }
    }
}