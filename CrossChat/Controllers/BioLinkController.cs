using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrossChat.Data;
using CrossChat.Data.Entities;
using CrossChat.Helpers;
using CrossChat.Infrastructure.Constants;
using CrossChat.Integrations.Services;
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
			[FromForm] string highlightColor,
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
			bioPage.HighlightColor = string.IsNullOrWhiteSpace(highlightColor) ? "#38bdf8" : highlightColor.Trim(); // <-- СОХРАНЯЕМ ЦВЕТ
			bioPage.UpdatedAt = DateTime.UtcNow;

			// 2.Загрузка аватарки(как обычно, сразу в Base64 в базу данных!)
			if (avatarImageFile != null && avatarImageFile.Length > 0)
			{
				var (mediaBase64, detectedTheme) = await ProcessCustomBackgroundFileAsync(avatarImageFile);
				bioPage.AvatarUrl = mediaBase64; // 400px для аватарки за глаза
			}

			// 3. Загрузка файла своего фона (Фото, GIF или ультра-легкое Видео)
			if ((themeType == "custom-image" || themeType == "custom-video") && bgImageFile != null && bgImageFile.Length > 0)
			{
				var (mediaBase64, detectedTheme) = await ProcessCustomBackgroundFileAsync(bgImageFile);
				bioPage.CustomBackgroundValue = mediaBase64;
				bioPage.ThemeType = detectedTheme; // Автоматически выставит custom-video или custom-image!
			}

			// 4. УМНОЕ ОБНОВЛЕНИЕ СПИСКА ССЫЛОК (БЕЗ УДАЛЕНИЯ И НАРУШЕНИЯ СВЯЗЕЙ С АНАЛИТИКОЙ!)
			if (!string.IsNullOrWhiteSpace(linksJson))
			{
				try
				{
					var incomingLinks = JsonSerializer.Deserialize<List<BioLinkInputDto>>(linksJson, new JsonSerializerOptions
					{
						PropertyNameCaseInsensitive = true
					}) ?? new List<BioLinkInputDto>();

					var incomingIds = incomingLinks.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();

					// А. Ссылки, которые пользователь реально удалил корзинкой 🗑️:
					var linksToRemove = bioPage.Links.Where(l => !incomingIds.Contains(l.Id)).ToList();
					if (linksToRemove.Any())
					{
						var removeIds = linksToRemove.Select(l => l.Id).ToList();

						// 🚀 ВАЖНО: Отвязываем удаляемые ссылки от статистики кликов (ставим null), исключая ошибку FK:
						await _db.BioPageEvents
							.Where(e => e.BioLinkItemId.HasValue && removeIds.Contains(e.BioLinkItemId.Value))
							.ExecuteUpdateAsync(s => s.SetProperty(e => e.BioLinkItemId, (int?)null));

						_db.BioLinkItems.RemoveRange(linksToRemove);
					}

					// Б. Обновляем существующие и добавляем новые:
					int order = 0;
					foreach (var item in incomingLinks)
					{
						if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Url))
							continue;

						string cleanUrl = NormalizeUrl(item.Url.Trim());

						// Если ссылка уже была — ОБНОВЛЯЕМ (сохраняя ID и счетчик кликов ClicksCount!):
						var existingLink = item.Id > 0 ? bioPage.Links.FirstOrDefault(l => l.Id == item.Id) : null;
						if (existingLink != null)
						{
							existingLink.Title = item.Title.Trim();
							existingLink.Url = cleanUrl;
							existingLink.IconType = string.IsNullOrWhiteSpace(item.IconType) ? "custom" : item.IconType.ToLowerInvariant();
							existingLink.IsHighlighted = item.IsHighlighted;
							existingLink.IsActive = item.IsActive;
							existingLink.IsIconOnly = item.IsIconOnly;
							existingLink.ImageUrl = item.ImageUrl;
							existingLink.SortOrder = order++;
						}
						// Если ссылка новая — добавляем:
						else
						{
							bioPage.Links.Add(new BioLinkItem
							{
								BioPageId = bioPage.Id,
								Title = item.Title.Trim(),
								Url = cleanUrl,
								IconType = string.IsNullOrWhiteSpace(item.IconType) ? "custom" : item.IconType.ToLowerInvariant(),
								IsHighlighted = item.IsHighlighted,
								IsActive = item.IsActive,
								IsIconOnly = item.IsIconOnly,
								ImageUrl = item.ImageUrl,
								SortOrder = order++
							});
						}
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "[BioLink] Ошибка умного обновления ссылок");
				}
			}

			// 🚀 АВТОМАТИЧЕСКАЯ УБОРКА ПРИЗРАЧНЫХ СТРАНИЦ ПРИ КАЖДОМ СОХРАНЕНИИ
			await CleanupGhostBioPagesAsync(userId, bioPage.Id, bioPage.ProfileId);

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
			var link = await _db.BioLinkItems.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId);
			if (link == null) return Ok();

			// 🚀 ФОНОВЫЙ ТРЕКИНГ КЛИКА
			var analyticsEvent = BioAnalyticsTracker.ParseRequest(HttpContext, link.BioPageId, link.Id, "click");
			_ = Task.Run(async () =>
			{
				try
				{
					using var scope = HttpContext.RequestServices.CreateScope();
					var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

					scopedDb.BioPageEvents.Add(analyticsEvent);
					await scopedDb.BioLinkItems.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.ClicksCount, l => l.ClicksCount + 1));
					await scopedDb.SaveChangesAsync();
				}
				catch { }
			});

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
				return NotFound();
			}

			// 🚀 АБСОЛЮТНО НЕ БЛОКИРУЮЩИЙ ТРЕКИНГ ПРОСМОТРА (0 мс задержки для пользователя!)
			var analyticsEvent = BioAnalyticsTracker.ParseRequest(HttpContext, bioPage.Id, null, "view");
			_ = Task.Run(async () =>
			{
				try
				{
					using var scope = HttpContext.RequestServices.CreateScope();
					var scopedDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

					scopedDb.BioPageEvents.Add(analyticsEvent);
					await scopedDb.BioPages.Where(b => b.Id == bioPage.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.ViewsCount, b => b.ViewsCount + 1));
					await scopedDb.SaveChangesAsync();
				}
				catch { }
			});

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

			// 1. Telegram Канал
			var tg = profile.TelegramChannelSettingsList.FirstOrDefault(x => x.ChannelId != 0);
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
			var ig = profile.InstagramSettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.Username));
			if (ig != null)
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
			var bsky = profile.BlueSkySettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.Handle));
			if (bsky != null)
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

			// 4. Threads (🚀 ТЕПЕРЬ ПОДТЯГИВАЕТСЯ ВСЕГДА ПРИ НАЛИЧИИ АККАУНТА!)
			var th = profile.ThreadsSettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.Username));
			if (th != null)
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
			var yt = profile.YouTubeSettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.ChannelId));
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
			var x = profile.XSettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.ScreenName));
			if (x != null)
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
			var fb = profile.FacebookSettingsList.FirstOrDefault(x => !string.IsNullOrEmpty(x.PageId));
			if (fb != null)
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

		// ==========================================================
		// 6. 📊 ДАШБОРД СКВОЗНОЙ АНАЛИТИКИ (/biolink/analytics)
		// ==========================================================
		[HttpGet("biolink/analytics")]
		public async Task<IActionResult> Analytics([FromQuery] int? profileId, [FromQuery] int days = 30)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
			int activeProfileId = profileId ?? await GetActiveProfileIdSafeAsync(_db, userId);

			var bioPage = await _db.BioPages
				.Include(b => b.Links)
				.FirstOrDefaultAsync(b => b.ProfileId == activeProfileId && b.Profile.UserId == userId);

			if (bioPage == null) return RedirectToAction("Index", new { profileId = activeProfileId });

			// 🚀 ПЛАН Б: Отложенное определение стран по сохраненным IP (только при входе админа!)
			await ResolvePendingCountriesAsync(bioPage.Id);

			var startDate = DateTime.UtcNow.AddDays(-days);

			var events = await _db.BioPageEvents
				.AsNoTracking()
				.Where(e => e.BioPageId == bioPage.Id && e.CreatedAtUtc >= startDate)
				.OrderByDescending(e => e.CreatedAtUtc)
				.ToListAsync();

			int totalViews = events.Count(e => e.EventType == "view");
			int totalClicks = events.Count(e => e.EventType == "click");
			double conversionRate = totalViews > 0 ? Math.Round(((double)totalClicks / totalViews) * 100.0, 1) : 0;

			// 1. Источники трафика
			var trafficSources = events
				.Where(e => e.EventType == "view")
				.GroupBy(e => e.TrafficSource)
				.Select(g => new { Source = g.Key, Count = g.Count() })
				.OrderByDescending(x => x.Count)
				.ToList();

			// 2. Топ нажатых кнопок
			var linkClicks = events
				.Where(e => e.EventType == "click" && e.BioLinkItemId.HasValue)
				.GroupBy(e => e.BioLinkItemId!.Value)
				.Select(g =>
				{
					var link = bioPage.Links.FirstOrDefault(l => l.Id == g.Key);
					return new
					{
						LinkId = g.Key,
						Title = link?.Title ?? "Удаленная ссылка",
						IconType = link?.IconType ?? "custom",
						Clicks = g.Count()
					};
				})
				.OrderByDescending(x => x.Clicks)
				.ToList();

			// 3. Страны
			var countries = events
				.Where(e => !string.IsNullOrEmpty(e.CountryCode))
				.GroupBy(e => e.CountryCode!)
				.Select(g => new { Country = g.Key.ToUpperInvariant(), Count = g.Count() })
				.OrderByDescending(x => x.Count)
				.Take(8)
				.ToList();

			// 4. Устройства и In-App браузеры
			int mobileCount = events.Count(e => e.DeviceType == "mobile" || e.DeviceType == "tablet");
			double mobilePercent = events.Count > 0 ? Math.Round(((double)mobileCount / events.Count) * 100.0, 1) : 0;
			int inAppBrowserCount = events.Count(e => e.Browser.Contains("inapp"));

			ViewBag.BioPage = bioPage;
			ViewBag.TotalViews = totalViews;
			ViewBag.TotalClicks = totalClicks;
			ViewBag.ConversionRate = conversionRate;
			ViewBag.TrafficSources = trafficSources;
			ViewBag.LinkClicks = linkClicks;
			ViewBag.Countries = countries;
			ViewBag.MobilePercent = mobilePercent;
			ViewBag.InAppBrowserCount = inAppBrowserCount;
			ViewBag.Days = days;
			ViewBag.RecentEvents = events.Take(40).ToList();

			return View("Analytics", bioPage);
		}

		/// <summary>
		/// ПЛАН Б: Бесплатное пакетированное определение стран по IP (до 15 штук за проход)
		/// </summary>
		private async Task ResolvePendingCountriesAsync(int bioPageId)
		{
			try
			{
				var pendingIps = await _db.BioPageEvents
					.Where(e => e.BioPageId == bioPageId && e.CountryCode == null && !string.IsNullOrEmpty(e.IpAddress) && e.IpAddress != "::1" && e.IpAddress != "127.0.0.1")
					.Select(e => e.IpAddress!)
					.Distinct()
					.Take(15)
					.ToListAsync();

				if (!pendingIps.Any()) return;

				using var client = new HttpClient();
				client.Timeout = TimeSpan.FromSeconds(3);

				foreach (var ip in pendingIps)
				{
					try
					{
						// Бесплатный эндпоинт без ключей (возвращает countryCode: US, RU, BR)
						var res = await client.GetFromJsonAsync<JsonElement>($"http://ip-api.com/json/{ip}?fields=countryCode");
						if (res.TryGetProperty("countryCode", out var cc))
						{
							string? code = cc.GetString();
							if (!string.IsNullOrEmpty(code))
							{
								await _db.BioPageEvents
									.Where(e => e.IpAddress == ip && e.CountryCode == null)
									.ExecuteUpdateAsync(s => s.SetProperty(e => e.CountryCode, code));
							}
						}
					}
					catch { }
				}
			}
			catch { }
		}

		/// <summary>
		/// 🧹 Автоматическая очистка: удаляет осиротевшие страницы, бережно сохраняя все живые профили пользователя
		/// </summary>
		private async Task CleanupGhostBioPagesAsync(int userId, int currentBioPageId, int currentProfileId)
		{
			try
			{
				// 1. Получаем список ID всех реально существующих профилей этого пользователя (Lana, CrossChat и т.д.)
				var activeProfileIds = await _db.Profile
					.Where(p => p.UserId == userId)
					.Select(p => p.Id)
					.ToListAsync();

				// 2. Ищем призрачные страницы, которые подлежат удалению:
				var ghostPageIds = await _db.BioPages
					.Where(b =>
						// а) Профиль вообще был удален из базы данных
						!_db.Profile.Any(p => p.Id == b.ProfileId)
						// б) Либо страница принадлежала этому пользователю, но её профиль удалили
						|| (b.Profile.UserId == userId && !activeProfileIds.Contains(b.ProfileId))
						// в) Либо это старый дубликат внутри текущего профиля (Id != currentBioPageId)
						|| (b.ProfileId == currentProfileId && b.Id != currentBioPageId)
					)
					.Select(b => b.Id)
					.ToListAsync();

				if (!ghostPageIds.Any()) return;

				_logger.LogInformation("🧹 [BioLink Cleanup] Найдено {Count} призрачных страниц. Удаление...", ghostPageIds.Count);

				// 🚀 КАСКАДНОЕ БЕЗОПАСНОЕ УДАЛЕНИЕ В EF CORE (.NET 9):
				// 1. Удаляем статистику кликов и просмотров
				await _db.BioPageEvents
					.Where(e => ghostPageIds.Contains(e.BioPageId))
					.ExecuteDeleteAsync();

				// 2. Удаляем привязанные кнопки-ссылки
				await _db.BioLinkItems
					.Where(l => ghostPageIds.Contains(l.BioPageId))
					.ExecuteDeleteAsync();

				// 3. Удаляем сами мертвые страницы BioPages (освобождая старые слоги в БД!)
				await _db.BioPages
					.Where(b => ghostPageIds.Contains(b.Id))
					.ExecuteDeleteAsync();

				_logger.LogInformation("✅ [BioLink Cleanup] Мертвые ссылки успешно удалены, живые профили в безопасности!");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "❌ [BioLink Cleanup] Ошибка при автоочистке призрачных страниц");
			}
		}

		/// <summary>
		/// Универсальный конвертер для живого фона: поддерживает Фото (JPEG), GIF-анимацию и сжатое Видео (MP4)
		/// </summary>
		private async Task<(string Base64Data, string ThemeType)> ProcessCustomBackgroundFileAsync(IFormFile file)
		{
			string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
			bool isVideo = file.ContentType.StartsWith("video/") || ext == ".mp4" || ext == ".mov" || ext == ".webm";
			bool isGif = file.ContentType == "image/gif" || ext == ".gif";

			// ===================================================================
			// 1. ЕСЛИ ЭТО ВИДЕО (MP4 / MOV / WEBM)
			// ===================================================================
			if (isVideo)
			{
				string tempInput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{ext}");
				try
				{
					using (var fs = new FileStream(tempInput, FileMode.Create))
					{
						await file.CopyToAsync(fs);
					}

					// Сжимаем через наш метод FFmpeg до 600-900 КБ
					string compressedPath = await VideoService.CompressVideoForBioBackgroundAsync(tempInput, _logger);
					byte[] videoBytes = await System.IO.File.ReadAllBytesAsync(compressedPath);

					try { if (System.IO.File.Exists(compressedPath)) System.IO.File.Delete(compressedPath); } catch { }

					string base64Video = $"data:video/mp4;base64,{Convert.ToBase64String(videoBytes)}";
					return (base64Video, "custom-video");
				}
				finally
				{
					try { if (System.IO.File.Exists(tempInput)) System.IO.File.Delete(tempInput); } catch { }
				}
			}

			// ===================================================================
			// 2. ЕСЛИ ЭТО GIF-АНИМАЦИЯ (НЕ ПЕРЕСОХРАНЯЕМ В JPEG!)
			// ===================================================================
			using var inMs = new MemoryStream();
			await file.CopyToAsync(inMs);
			var rawBytes = inMs.ToArray();

			if (isGif)
			{
				_logger.LogInformation("🎬 [BioLink] Сохранена живая GIF-анимация ({Size} байт)", rawBytes.Length);
				return ($"data:image/gif;base64,{Convert.ToBase64String(rawBytes)}", "custom-image");
			}

			// ===================================================================
			// 3. ЕСЛИ ЭТО ОБЫЧНОЕ ФОТО (Сжимаем в JPEG как обычно)
			// ===================================================================
			try
			{
				using var image = SixLabors.ImageSharp.Image.Load(rawBytes);

				int maxDim = 1200;
				if (image.Width > maxDim || image.Height > maxDim)
				{
					image.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
					{
						Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
						Size = new SixLabors.ImageSharp.Size(maxDim, maxDim)
					}));
				}

				using var outMs = new MemoryStream();
				image.SaveAsJpeg(outMs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
				return ($"data:image/jpeg;base64,{Convert.ToBase64String(outMs.ToArray())}", "custom-image");
			}
			catch
			{
				return ($"data:{file.ContentType};base64,{Convert.ToBase64String(rawBytes)}", "custom-image");
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
		public int Id { get; set; } = 0;
		public string Title { get; set; } = string.Empty;
		public string Url { get; set; } = string.Empty;
		public string IconType { get; set; } = "custom";
		public bool IsHighlighted { get; set; } = false;
		public bool IsActive { get; set; } = true;
		public bool IsIconOnly { get; set; } = false; // 🚀 НОВЫЙ ФЛАГ
		public string? ImageUrl { get; set; } // 🚀 НОВОЕ ПОЛЕ БАННЕРА
	}
}