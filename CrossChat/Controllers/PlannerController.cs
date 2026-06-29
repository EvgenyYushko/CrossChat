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
		private readonly ILogger<PlannerController> _logger;
		private const bool SHRIK_IMAGES = false;

		public PlannerController(AppDbContext db, IPostService postService, ILogger<PlannerController> logger)
		{
			_db = db;
			_postService = postService;
			_logger = logger;
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
		[RequestSizeLimit(100 * 1024 * 1024)] // Устанавливает лимит Kestrel в 100 МБ
		[RequestFormLimits(MultipartBodyLengthLimit = 100 * 1024 * 1024)] // Устанавливает лимит формы в 100 МБ
		public async Task<IActionResult> Create(
			[FromForm] int profileId, 
			[FromForm] NetworkType networkType, 
			[FromForm] string caption, 
			[FromForm] DateTime showDate, 
			List<IFormFile> images)
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
			if (images != null && images.Count > 0)
			{
				if (SHRIK_IMAGES)
				{
					_logger.LogInformation("=== СЖАТИЕ ИЗОБРАЖЕНИЙ: СОЗДАНИЕ ПОСТА ===");
					foreach (var file in images)
					{
						try
						{
							var compressResult = await ImageHelper.CompressAndConvertToBase64Async(file);
							post.Images.Add(compressResult.Base64);

							// Выводим развернутую статистику до и после
							_logger.LogInformation(
								"Файл: {FileName}\n" +
								"  [ДО]: {OrigWidth}x{OrigHeight} px | Размер: {OrigSize:F3} МБ\n" +
								"  [ПОСЛЕ]: {CompWidth}x{CompHeight} px | Размер JPEG: {CompSize:F3} МБ\n" +
								"  [В БД (Base64)]: Символов: {B64Length} | Итоговый вес в БД: {B64DbSize:F3} МБ",
								file.FileName,
								compressResult.OriginalWidth, compressResult.OriginalHeight, compressResult.OriginalSizeMb,
								compressResult.CompressedWidth, compressResult.CompressedHeight, compressResult.CompressedSizeMb,
								compressResult.Base64.Length, compressResult.Base64DbSizeMb);
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при сжатии изображения {FileName}", file.FileName);
						}
					}
					_logger.LogInformation("=========================================");
				}
				else
				{
					foreach (var file in images)
					{
						using var ms = new MemoryStream();
						await file.CopyToAsync(ms);
						post.Images.Add(Convert.ToBase64String(ms.ToArray()));
					}
				}
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
			var options = new JsonSerializerOptions
			{
				Converters = { new JsonStringEnumConverter() } // ЭТО СДЕЛАЕТ КЛЮЧИ ТЕКСТОВЫМИ
															   // Отключает агрессивное экранирование Base64, снижая нагрузку на память в разы
				,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
		[RequestSizeLimit(100 * 1024 * 1024)] // Устанавливает лимит Kestrel в 100 МБ
		[RequestFormLimits(MultipartBodyLengthLimit = 100 * 1024 * 1024)] // Устанавливает лимит формы в 100 МБ
		public async Task<IActionResult> Update(
			Guid id,
			[FromForm] int profileId,
			[FromForm] string networkType,
			[FromForm] string caption,
			[FromForm] DateTime showDate,
			[FromForm] List<string> keptImages, // Старые сохраненные картинки в Base64
			[FromForm] List<IFormFile> images)   // Новые добавленные файлы
		{
			// 1. Получаем существующий пост из базы данных
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// 2. Обновляем базовые данные
			post.ShowDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			// Обновляем текст для выбранной соцсети
			var netType = Enum.Parse<NetworkType>(networkType);
			if (post.Networks.ContainsKey(netType))
			{
				post.Networks[netType].Caption = caption;
			}

			// 3. Обновляем список картинок поста
			// Перезаписываем список картинок только теми старыми картинками, которые пользователь не удалил на фронтенде
			post.Images = keptImages ?? new List<string>();

			// Добавляем новые картинки, если они были загружены
			if (images != null && images.Count > 0)
			{
				if (SHRIK_IMAGES)
				{
					_logger.LogInformation("=== СЖАТИЕ ИЗОБРАЖЕНИЙ: ОБНОВЛЕНИЕ ПОСТА ===");
					foreach (var file in images)
					{
						try
						{
							var compressResult = await ImageHelper.CompressAndConvertToBase64Async(file);
							post.Images.Add(compressResult.Base64);

							_logger.LogInformation(
								"Файл: {FileName}\n" +
								"  [ДО]: {OrigWidth}x{OrigHeight} px | Размер: {OrigSize:F3} МБ\n" +
								"  [ПОСЛЕ]: {CompWidth}x{CompHeight} px | Размер JPEG: {CompSize:F3} МБ\n" +
								"  [В БД (Base64)]: Символов: {B64Length} | Итоговый вес в БД: {B64DbSize:F3} МБ",
								file.FileName,
								compressResult.OriginalWidth, compressResult.OriginalHeight, compressResult.OriginalSizeMb,
								compressResult.CompressedWidth, compressResult.CompressedHeight, compressResult.CompressedSizeMb,
								compressResult.Base64.Length, compressResult.Base64DbSizeMb);
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при сжатии изображения {FileName}", file.FileName);
						}
					}
					_logger.LogInformation("===========================================");
				}
				else
				{
					foreach (var file in images)
					{
						using var ms = new MemoryStream();
						await file.CopyToAsync(ms);
						post.Images.Add(Convert.ToBase64String(ms.ToArray()));
					}
				}
			}

			// 4. Сохраняем изменения в базе
			await _postService.UpdatePostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType });
		}
	}
}
