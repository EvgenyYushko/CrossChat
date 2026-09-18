using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Worker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Controllers
{
	[Authorize]
	[Route("trends")]
	public class TrendsController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly TrendRadarService _radarService;

		public TrendsController(AppDbContext db, TrendRadarService radarService)
		{
			_db = db;
			_radarService = radarService;
		}

		[HttpGet]
		public async Task<IActionResult> Index(int? tagId, string? mediaFilter)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			// 1. Проверяем наличие рабочей связки Facebook + Instagram
			var (fbSettings, igUserId) = await _radarService.GetOrDiscoverWorkingPairAsync(userId);
			ViewBag.IsConnected = fbSettings != null && !string.IsNullOrEmpty(igUserId);
			ViewBag.ConnectedPageName = fbSettings?.PageName;
			ViewBag.ConnectedIgId = igUserId;

			// 2. Достаем все отслеживаемые теги пользователя
			var tags = await _db.TrackedHashtags
				.Include(t => t.Posts)
				.Where(t => t.UserId == userId)
				.OrderByDescending(t => t.CreatedAt)
				.ToListAsync();

			ViewBag.Tags = tags;
			ViewBag.SelectedTagId = tagId;
			ViewBag.MediaFilter = mediaFilter;

			// 3. Выборка постов
			IQueryable<ViralPost> postsQuery = _db.ViralPosts
				.Include(p => p.TrackedHashtag)
				.Where(p => p.TrackedHashtag.UserId == userId);

			if (tagId.HasValue && tagId.Value > 0)
			{
				postsQuery = postsQuery.Where(p => p.TrackedHashtagId == tagId.Value);
			}

			if (mediaFilter == "video")
			{
				postsQuery = postsQuery.Where(p => p.MediaType == "VIDEO");
			}
			else if (mediaFilter == "image")
			{
				postsQuery = postsQuery.Where(p => p.MediaType == "IMAGE" || p.MediaType == "CAROUSEL_ALBUM");
			}

			// Сортируем: самые популярные сверху
			var posts = await postsQuery
				.OrderByDescending(p => p.LikeCount)
				.Take(50)
				.ToListAsync();

			return View(posts);
		}

		/// <summary>
		/// Кнопка на фронтенде "Проверить связку сейчас"
		/// </summary>
		[HttpPost("check-link")]
		public async Task<IActionResult> CheckLink()
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var (fb, igId) = await _radarService.GetOrDiscoverWorkingPairAsync(userId);

			return RedirectToAction("Index");
		}

		/// <summary>
		/// Добавление нового хештега для отслеживания
		/// </summary>
		[HttpPost("add-tag")]
		public async Task<IActionResult> AddTag([FromForm] string tag)
		{
			if (string.IsNullOrWhiteSpace(tag)) return RedirectToAction("Index");

			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			string cleanTag = tag.Replace("#", "").Trim().ToLowerInvariant();

			var existing = await _db.TrackedHashtags
				.FirstOrDefaultAsync(t => t.UserId == userId && t.Tag == cleanTag);

			if (existing == null)
			{
				var newTag = new TrackedHashtag
				{
					UserId = userId,
					Tag = cleanTag,
					IsAutoSync = true,
					CreatedAt = DateTime.UtcNow
				};
				_db.TrackedHashtags.Add(newTag);
				await _db.SaveChangesAsync();

				// Сразу запускаем первый поиск постов!
				await _radarService.SyncHashtagPostsAsync(newTag.Id, userId);
				return RedirectToAction("Index", new { tagId = newTag.Id });
			}

			return RedirectToAction("Index", new { tagId = existing.Id });
		}

		/// <summary>
		/// Ручное обновление постов по тегу
		/// </summary>
		[HttpPost("sync-tag/{id}")]
		public async Task<IActionResult> SyncTag(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			await _radarService.SyncHashtagPostsAsync(id, userId);

			return RedirectToAction("Index", new { tagId = id });
		}

		/// <summary>
		/// Переключение авто-синхронизации (Активен / На паузе)
		/// </summary>
		[HttpPost("toggle-sync/{id}")]
		public async Task<IActionResult> ToggleSync(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var tag = await _db.TrackedHashtags.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

			if (tag != null)
			{
				tag.IsAutoSync = !tag.IsAutoSync;
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index", new { tagId = id });
		}

		/// <summary>
		/// Удаление хештега и связанных постов
		/// </summary>
		[HttpPost("delete-tag/{id}")]
		public async Task<IActionResult> DeleteTag(int id)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			var tag = await _db.TrackedHashtags
				.Include(t => t.Posts)
				.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

			if (tag != null)
			{
				_db.ViralPosts.RemoveRange(tag.Posts);
				_db.TrackedHashtags.Remove(tag);
				await _db.SaveChangesAsync();
			}

			return RedirectToAction("Index");
		}
	}
}