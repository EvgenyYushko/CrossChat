using System.Security.Claims;
using CrossChat.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("profiles")]
	public class ProfilesController : Controller
	{
		private AppDbContext _db;
		private ILogger<ProfilesController> _logger;

		public ProfilesController(AppDbContext db, ILogger<ProfilesController> logger)
		{
			_db = db;
			_logger = logger;
		}

		[Authorize]
		public async Task<IActionResult> Index()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var user = await _db.Users
				.Include(p => p.Profile)
				.FirstOrDefaultAsync(u => u.Id == userId);

			var authUser = new AuthUser();
			authUser.User = user;

			return View(authUser); 
		}		
	}
}
