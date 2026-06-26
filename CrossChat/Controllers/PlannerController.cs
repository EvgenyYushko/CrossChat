using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization; 
using CrossChat.Data;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static CrossChat.Helpers.TimeZoneHelper;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("planner")]
	public class PlannerController : Controller
	{
		private readonly AppDbContext _db;
		private readonly IPostService _postService;

		public PlannerController(AppDbContext db, IPostService postService)
		{
			_db = db;
			_postService = postService;
		}

		[HttpGet]
		public IActionResult Index(int profileId, string network)
		{
			// Передаем параметры во View через ViewBag
			ViewBag.ProfileId = profileId;
			ViewBag.Network = network;

			// Ищет Views/Planner/Index.cshtml
			return View();
		}

		[HttpGet("events")]
		public async Task<IActionResult> GetEvents(int profileId, string networkType)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var netType = Enum.Parse<NetworkType>(networkType);
			int netTypeId = (int)netType;

			var posts = await _db.Posts
				.Include(p => p.NetworkStates)
				.Where(p => p.ProfileId == profileId &&
							p.NetworkStates.Any(ns => ns.NetworkType == netTypeId && ns.Status != (int)SocialStatus.None))
				.ToListAsync();

			// Превращаем наши посты в формат FullCalendar
			var events = posts.Select(p => new
			{
				id = p.Id,
				title = p.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netTypeId)?.Caption ?? "Пост",
				start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"), // ISO формат
				backgroundColor = p.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netTypeId)?.Status == (int)SocialStatus.Published ? "#10b981" : "#fbbf24"
			});

			return Json(events);
		}

		[HttpPost("create")]
		public async Task<IActionResult> Create(int profileId, NetworkType networkType, string caption, DateTime showDate, List<IFormFile> images)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var utcDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			// 1. Создаем BlogPost (Domain Model)
			var post = new BlogPost
			{
				Id = Guid.NewGuid(),
				ProfileId = profileId,
				CreatedAt = DateTimeNow,
				ShowDate = utcDate,
				Access = AccessLevel.Public
			};

			// 2. Обработка картинок
			foreach (var file in images)
			{
				using var ms = new MemoryStream();
				await file.CopyToAsync(ms);
				post.Images.Add(Convert.ToBase64String(ms.ToArray()));
			}

			// 3. Добавляем состояние сети
			post.Networks[networkType] = new NetworkPostData
			{
				Status = SocialStatus.Pending,
				Caption = caption
			};

			// 4. Сохраняем через твой PostService
			await _postService.AddPostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType });
		}

		[HttpGet("get/{id}")]
		public async Task<IActionResult> GetPost(Guid id)
		{
			var post = await _postService.GetPostByIdAsync(id);
			// Настраиваем сериализатор
			var options = new JsonSerializerOptions {
				Converters = { new JsonStringEnumConverter() } // ЭТО СДЕЛАЕТ КЛЮЧИ ТЕКСТОВЫМИ
			};
    
			return post != null ? Json(post, options) : NotFound();
		}

		[HttpPost("delete/{id}")]
		public async Task<IActionResult> Delete(Guid id)
		{
			await _postService.DeletePostAsync(id);
			return Ok();
		}

		[HttpPost("update/{id}")]
		public async Task<IActionResult> Update(Guid id, [FromForm] int profileId, [FromForm] string networkType, [FromForm] string caption, [FromForm] DateTime showDate)
		{
			// 1. Получаем существующий пост
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// 2. Обновляем данные
			post.ShowDate = showDate; // Обновляем дату

			// Обновляем текст для нужной соцсети
			var netType = Enum.Parse<NetworkType>(networkType);
			if (post.Networks.ContainsKey(netType))
			{
				post.Networks[netType].Caption = caption;
			}

			// 3. Сохраняем
			await _postService.UpdatePostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType });
		}
	}
}
