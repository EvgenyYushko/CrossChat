using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Infrastructure.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace CrossChat.Controllers
{
	[Authorize]
	public class BioLinkController : BaseController
	{
		private readonly AppDbContext _db;
		private readonly IWebHostEnvironment _env;
		private readonly ILogger<BioLinkController> _logger;

		// Зарезервированные системные пути, которые нельзя занимать в качестве slug:
		private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
		{
			"inbox", "planner", "instagram", "facebook", "threads", "bluesky", "telegram",
			"telegram-channel", "x", "youtube", "auth", "profile", "profiles", "trends",
			"api", "images", "css", "js", "lib", "temp_media", "uploads", "loghub", "inboxhub",
			"admin", "login", "register", "logout", "privacy", "terms", "biolink"
		};

		public BioLinkController(AppDbContext db, IWebHostEnvironment env, ILogger<BioLinkController> logger)
		{
			_db = db;
			_env = env;
			_logger = logger;
		}

		// ==========================================================
		// 1. КОНСТРУКТОР СТРАНИЦЫ В ЛИЧНОМ КАБИНЕТЕ (/biolink)
		// ==========================================================
		[HttpGet("biolink")]
		public async Task<IActionResult> Index([FromQuery] int? profileId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			int activeProfileId = profileId ?? await GetActiveProfileIdSafeAsync(_db, userId);

			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramChannelSettingsList)
				.Include(p => p.BlueSkySettingsList)
				.Include(p => p.YouTubeSettingsList)
				.FirstOrDefaultAsync(p => p.Id == activeProfileId && p.UserId == userId);

			if (profile == null) return NotFound("Профиль не найден");

			// Ищем существующую страницу-переходник для этого профиля
			var bioPage = await _db.BioPages
				.Include(b => b.Links.OrderBy(l => l.SortOrder))
				.FirstOrDefaultAsync(b => b.ProfileId == activeProfileId);

			// Если еще нет — создаем начальный черновик
			if (bioPage == null)
			{
				string defaultSlug = GenerateInitialSlug(profile.Name);

				bioPage = new BioPage
				{
					ProfileId = profile.Id,
					Title = profile.Name,
					Slug = defaultSlug,
					Bio = "Добро пожаловать на мою страницу! Выберите удобный способ связи 👇",
					AvatarUrl = profile.AvatarUrl,
					ThemeType = "gradient-dark"
				};

				_db.BioPages.Add(bioPage);
				await _db.SaveChangesAsync();

				// Автоматически первично наполняем подключенными соцсетями
				await AutoFillLinksInternalAsync(bioPage, profile);
			}

			ViewBag.Profile = profile;
			ViewBag.Profiles = await _db.Profile.Where(p => p.UserId == userId).ToListAsync();
			ViewBag.AppUrl = AppConstants.APP_URL;

			return View("Index", bioPage);
		}

		// ==========================================================
		// 2. СОХРАНЕНИЕ НАСТРОЕК СТРАНИЦЫ И ССЫЛОК
		// ==========================================================
		[HttpPost("biolink/save")]
		[RequestSizeLimit(30 * 1024 * 1024)]
		public async Task<IActionResult> Save(
			[FromForm] int bioPageId,
			[FromForm] string slug,
			[FromForm] string title,
			[FromForm] string? bio,
			[FromForm] string themeType,
			[FromForm] string? customBackgroundValue,
			[FromForm] string linksJson,
			IFormFile? bgImageFile,
			IFormFile? avatarImageFile)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var bioPage = await _db.BioPages
				.Include(b => b.Profile)
				.Include(b => b.Links)
				.FirstOrDefaultAsync(b => b.Id == bioPageId && b.Profile.UserId == userId);

			if (bioPage == null) return NotFound();

			// 1. Валидация и нормализация Slug (только латиница, цифры, дефис и подчеркивание)
			string cleanSlug = Slugify(slug);
			if (string.IsNullOrWhiteSpace(cleanSlug) || cleanSlug.Length < 2)
			{
				return BadRequest("Короткая ссылка должна содержать минимум 2 символа (латиница, цифры, дефис).");
			}

			if (ReservedSlugs.Contains(cleanSlug))
			{
				return BadRequest($"Ссылка '{cleanSlug}' зарезервирована системой. Пожалуйста, выберите другое имя.");
			}

			// Проверка на уникальность
			bool isSlugTaken = await _db.BioPages.AnyAsync(b => b.Slug == cleanSlug && b.Id != bioPage.Id);
			if (isSlugTaken)
			{
				return BadRequest($"Ссылка '{cleanSlug}' уже занята другим пользователем. Выберите другой вариант.");
			}

			bioPage.Slug = cleanSlug;
			bioPage.Title = string.IsNullOrWhiteSpace(title) ? bioPage.Profile.Name : title.Trim();
			bioPage.Bio = bio?.Trim();
			bioPage.ThemeType = themeType;
			bioPage.CustomBackgroundValue = customBackgroundValue;
			bioPage.UpdatedAt = DateTime.UtcNow;

			// 2.Загрузка аватарки(как обычно, сразу в Base64 в базу данных!)
			if (avatarImageFile != null && avatarImageFile.Length > 0)
			{
				bioPage.AvatarUrl = await ConvertFileToBase64CompressedAsync(avatarImageFile, 400); // 400px для аватарки за глаза
			}

			// 3. Загрузка файла своего фона (в Base64)
			if (themeType == "custom-image" && bgImageFile != null && bgImageFile.Length > 0)
			{
				bioPage.CustomBackgroundValue = await ConvertFileToBase64CompressedAsync(bgImageFile, 1200); // 1200px для фона
			}

			// 4. Обновление списка ссылок из JSON
			if (!string.IsNullOrWhiteSpace(linksJson))
			{
				try
				{
					var incomingLinks = JsonSerializer.Deserialize<List<BioLinkInputDto>>(linksJson, new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true
					});

					if (incomingLinks != null)
					{
						// Очищаем старые ссылки
						_db.BioLinkItems.RemoveRange(bioPage.Links);
						bioPage.Links.Clear();

						int order = 0;
						foreach (var item in incomingLinks)
						{
							if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Url))
								continue;

							bioPage.Links.Add(new BioLinkItem
							{
								BioPageId = bioPage.Id,
								Title = item.Title.Trim(),
								Url = NormalizeUrl(item.Url.Trim()),
								IconType = string.IsNullOrWhiteSpace(item.IconType) ? "custom" : item.IconType.ToLowerInvariant(),
								IsHighlighted = item.IsHighlighted,
								IsActive = item.IsActive,
								SortOrder = order++
							});
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[BioLink] Ошибка десериализации ссылок");
				}
			}

			await _db.SaveChangesAsync();
			_logger.LogInformation("✅ [BioLink] Страница {Slug} успешно сохранена для профиля {ProfileId}", bioPage.Slug, bioPage.ProfileId);

			return Ok(new
			{
				success = true,
				slug = bioPage.Slug,
				publicUrl = $"{AppConstants.APP_URL}/{bioPage.Slug}",
				avatarUrl = bioPage.AvatarUrl,
				backgroundValue = bioPage.CustomBackgroundValue
			});
		}

		// ==========================================================
		// 3. ✨ КИЛЛЕР-ФИЧА: АВТОЗАПОЛНЕНИЕ ИЗ ПОДКЛЮЧЕННЫХ СОЦСЕТЕЙ
		// ==========================================================
		[HttpPost("biolink/autofill")]
		public async Task<IActionResult> AutoFill([FromQuery] int bioPageId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			var bioPage = await _db.BioPages
				.Include(b => b.Profile)
				.ThenInclude(p => p.InstagramSettingsList)
				.Include(b => b.Profile.FacebookSettingsList)
				.Include(b => b.Profile.TelegramChannelSettingsList)
				.Include(b => b.Profile.ThreadsSettingsList)
				.Include(b => b.Profile.XSettingsList)
				.Include(b => b.Profile.BlueSkySettingsList)
				.Include(b => b.Profile.YouTubeSettingsList)
				.Include(b => b.Links)
				.FirstOrDefaultAsync(b => b.Id == bioPageId && b.Profile.UserId == userId);

			if (bioPage == null) return NotFound();

			await AutoFillLinksInternalAsync(bioPage, bioPage.Profile);

			return RedirectToAction("Index", new { profileId = bioPage.ProfileId, autofilled = "true" });
		}

		// ==========================================================
		// 4. ТРЕКИНГ КЛИКОВ ПО ССЫЛКАМ (АНОНИМНЫЙ)
		// ==========================================================
		[HttpPost("~/biolink/click/{linkId}")]
		[AllowAnonymous]
		public async Task<IActionResult> TrackClick(int linkId)
		{
			await _db.BioLinkItems
				.Where(l => l.Id == linkId)
				.ExecuteUpdateAsync(s => s.SetProperty(l => l.ClicksCount, l => l.ClicksCount + 1));

			return Ok();
		}

		// ==========================================================
		// 5. 🌍 ПУБЛИЧНАЯ СТРАНИЦА-ПЕРЕХОДНИК (crosschat.ru/{slug})
		// ==========================================================
		[HttpGet("~/{slug}")]
		[AllowAnonymous]
		public async Task<IActionResult> PublicPage(string slug)
		{
			if (string.IsNullOrWhiteSpace(slug) || ReservedSlugs.Contains(slug))
			{
				return NotFound();
			}

			string cleanSlug = slug.Trim().ToLowerInvariant();

			var bioPage = await _db.BioPages
				.Include(b => b.Links.Where(l => l.IsActive).OrderBy(l => l.SortOrder))
				.FirstOrDefaultAsync(b => b.Slug == cleanSlug && b.IsActive);

			if (bioPage == null)
			{
				return NotFound(); // Если нет такой страницы — отдаем стандартный 404
			}

			// Тихо инкрементируем счётчик просмотров
			await _db.BioPages
				.Where(b => b.Id == bioPage.Id)
				.ExecuteUpdateAsync(s => s.SetProperty(b => b.ViewsCount, b => b.ViewsCount + 1));

			return View("~/Views/BioLink/PublicBio.cshtml", bioPage);
		}

		// ==========================================================
		// ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
		// ==========================================================
		private async Task AutoFillLinksInternalAsync(BioPage bioPage, Profile profile)
		{
			_db.BioLinkItems.RemoveRange(bioPage.Links);
			bioPage.Links.Clear();

			int sort = 0;

			// 1. Telegram Канал (ЧИСТОЕ ИМЯ БЕЗ СМАЙЛИКА В ТЕКСТЕ)
			var tg = profile.TelegramChannelSettingsList.FirstOrDefault(x => x.IsActive);
			if (tg != null)
			{
				string tgUrl = !string.IsNullOrEmpty(tg.ChannelUsername)
					? $"https://t.me/{tg.ChannelUsername.Replace("@", "")}"
					: "https://t.me/";

				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = !string.IsNullOrEmpty(tg.ChannelTitle) ? tg.ChannelTitle : "Telegram канал",
					Url = tgUrl,
					IconType = "telegram",
					IsHighlighted = true,
					SortOrder = sort++
				});
			}

			// 2. Instagram
			var ig = profile.InstagramSettingsList.FirstOrDefault(x => x.IsActive);
			if (ig != null && !string.IsNullOrEmpty(ig.Username))
			{
				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = $"Instagram: @{ig.Username}",
					Url = $"https://instagram.com/{ig.Username.Replace("@", "")}",
					IconType = "instagram",
					SortOrder = sort++
				});
			}

			// 3. BlueSky
			var bsky = profile.BlueSkySettingsList.FirstOrDefault(x => x.IsActive);
			if (bsky != null && !string.IsNullOrEmpty(bsky.Handle))
			{
				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = $"BlueSky: @{bsky.Handle}",
					Url = $"https://bsky.app/profile/{bsky.Handle}",
					IconType = "bluesky",
					SortOrder = sort++
				});
			}

			// 4. Threads
			var th = profile.ThreadsSettingsList.FirstOrDefault(x => x.IsActive);
			if (th != null && !string.IsNullOrEmpty(th.Username))
			{
				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = $"Threads: @{th.Username}",
					Url = $"https://threads.net/@{th.Username.Replace("@", "")}",
					IconType = "threads",
					SortOrder = sort++
				});
			}

			// 5. YouTube
			var yt = profile.YouTubeSettingsList.FirstOrDefault(x => x.IsActive);
			if (yt != null)
			{
				string ytUrl = !string.IsNullOrEmpty(yt.CustomUrl)
					? $"https://youtube.com/{yt.CustomUrl}"
					: $"https://youtube.com/channel/{yt.ChannelId}";

				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = !string.IsNullOrEmpty(yt.ChannelTitle) ? yt.ChannelTitle : "YouTube канал",
					Url = ytUrl,
					IconType = "youtube",
					SortOrder = sort++
				});
			}

			// 6. X (Twitter)
			var x = profile.XSettingsList.FirstOrDefault(x => x.IsActive);
			if (x != null && !string.IsNullOrEmpty(x.ScreenName))
			{
				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = $"X: @{x.ScreenName}",
					Url = $"https://x.com/{x.ScreenName.Replace("@", "")}",
					IconType = "x",
					SortOrder = sort++
				});
			}

			// 7. Facebook
			var fb = profile.FacebookSettingsList.FirstOrDefault(x => x.IsActive);
			if (fb != null && !string.IsNullOrEmpty(fb.PageId))
			{
				bioPage.Links.Add(new BioLinkItem
				{
					BioPageId = bioPage.Id,
					Title = !string.IsNullOrEmpty(fb.PageName) ? fb.PageName : "Facebook страница",
					Url = $"https://facebook.com/{fb.PageId}",
					IconType = "facebook",
					SortOrder = sort++
				});
			}

			await _db.SaveChangesAsync();
		}

		/// <summary>
		/// Преобразует загруженный файл в чистый легкий Base64 без сохранения на диск (как обычно во всем проекте)
		/// </summary>
		private static async Task<string> ConvertFileToBase64CompressedAsync(IFormFile file, int maxDimension)
		{
			using var inMs = new MemoryStream();
			await file.CopyToAsync(inMs);
			var rawBytes = inMs.ToArray();

			try
			{
				// Оптимизируем размер через ImageSharp, чтобы не раздувать базу данных
				using var image = SixLabors.ImageSharp.Image.Load(rawBytes);

				if (image.Width > maxDimension || image.Height > maxDimension)
				{
					image.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
					{
						Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
						Size = new SixLabors.ImageSharp.Size(maxDimension, maxDimension)
					}));
				}

				using var outMs = new MemoryStream();
				image.SaveAsJpeg(outMs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
				return $"data:image/jpeg;base64,{Convert.ToBase64String(outMs.ToArray())}";
			}
			catch
			{
				// Фоллбек, если это не картинка
				return $"data:{file.ContentType};base64,{Convert.ToBase64String(rawBytes)}";
			}
		}

		private static string Slugify(string text)
		{
			if (string.IsNullOrWhiteSpace(text)) return "";
			string s = text.Trim().ToLowerInvariant();
			s = Regex.Replace(s, @"\s+", "-");
			s = Regex.Replace(s, @"[^a-z0-9\-_]", "");
			return s;
		}

		private static string GenerateInitialSlug(string name)
		{
			string s = Slugify(name);
			return string.IsNullOrWhiteSpace(s) ? "page-" + Guid.NewGuid().ToString("N")[..6] : s;
		}

		private static string NormalizeUrl(string url)
		{
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
				!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				return "https://" + url;
			}
			return url;
		}
	}

	public class BioLinkInputDto
	{
		public string Title { get; set; } = string.Empty;
		public string Url { get; set; } = string.Empty;
		public string IconType { get; set; } = "custom";
		public bool IsHighlighted { get; set; } = false;
		public bool IsActive { get; set; } = true;
	}
}