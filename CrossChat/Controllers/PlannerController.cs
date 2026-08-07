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
using static CrossChat.Worker.Helpers.TimeZoneHelper;

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
		public async Task<IActionResult> Index(int profileId, string network, int? botId)
		{
			// Загружаем профиль со всеми его связанными списками настроек соцсетей
			var profile = await _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.TelegramChannelSettingsList)
				.Include(p => p.BlueSkySettingsList)
				.FirstOrDefaultAsync(p => p.Id == profileId);

			if (profile == null) return NotFound();

			// Передаем параметры во View через ViewBag
			ViewBag.ProfileId = profileId;
			ViewBag.Network = network;
			ViewBag.BotId = botId; // <-- ЗАПОМИНАЕМ конкретный ID подключенного аккаунта/бота!

			return View(profile);
		}

		[HttpGet("events")]
		public async Task<IActionResult> GetEvents(int profileId, string networkType, int? botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			if (networkType == "All")
			{
				// ОБЩИЙ РЕЖИМ: Выбираем посты, у которых активно хотя бы одно направление
				var posts = await _db.Posts
					.Include(p => p.NetworkStates)
					.Where(p => p.ProfileId == profileId &&
								p.NetworkStates.Any(ns => ns.Status != (int)SocialStatus.None))
					.ToListAsync();

				var events = posts.Select(p =>
				{
					var activeStates = p.NetworkStates.Where(ns => ns.Status != (int)SocialStatus.None).ToList();
					var mainCaption = activeStates.FirstOrDefault()?.Caption ?? "Пост";

					return new
					{
						id = p.Id,
						title = mainCaption,
						start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"),
						backgroundColor = "#4f46e5",
						network = "All",
						// Отдаем массив ключей "{Соцсеть}_{BotId}"
						activeNetworks = activeStates.Select(ns => $"{((NetworkType)ns.NetworkType).ToString()}_{ns.BotId}").ToList()
					};
				});

				return Json(events);
			}
			else
			{
				// ОДИНОЧНЫЙ РЕЖИМ (Instagram, Telegram...): Фильтруем строго по конкретному BotId!
				var netType = Enum.Parse<NetworkType>(networkType);
				int netTypeId = (int)netType;

				// Если BotId не передан явно в запросе календаря, находим первого активного бота
				var finalBotId = botId ?? FindFirstActiveBotId(profileId, netType);

				var posts = await _db.Posts
					.Include(p => p.NetworkStates)
					.Where(p => p.ProfileId == profileId &&
								p.NetworkStates.Any(ns => ns.NetworkType == netTypeId &&
														  ns.BotId == finalBotId && // <-- Фильтр по BotId!
														  ns.Status != (int)SocialStatus.None))
					.ToListAsync();

				var events = posts.Select(p => new
				{
					id = p.Id,
					title = p.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netTypeId && ns.BotId == finalBotId)?.Caption ?? "Пост",
					start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"),
					backgroundColor = p.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netTypeId && ns.BotId == finalBotId)?.Status == (int)SocialStatus.Published ? "#10b981" : "#fbbf24",
					network = networkType
				});

				return Json(events);
			}
		}

		[HttpPost("create")]
		[RequestSizeLimit(100 * 1024 * 1024)] // Устанавливает лимит Kestrel в 100 МБ
		[RequestFormLimits(MultipartBodyLengthLimit = 100 * 1024 * 1024)] // Устанавливает лимит формы в 100 МБ
		public async Task<IActionResult> Create(
			[FromForm] int profileId,
			[FromForm] string networkType, // Унифицировали: теперь тип string вместо NetworkType
			[FromForm] List<string> selectedNetworks, // Чекбоксы выбранных сетей из формы
			[FromForm] string caption,
			[FromForm] DateTime showDate,
			[FromForm] int? botId,
			[FromForm] List<string> originalDimensions,
			[FromForm] List<string> compressedDimensions,
			[FromForm] List<long> originalSizes,
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

			// 2. Наполнение и валидация соцсетей (Вызов общего хелпера)
			if (!FillNetworkData(post, networkType, selectedNetworks, caption, botId))
			{
				return BadRequest("Пожалуйста, выберите хотя бы одну социальную сеть для публикации.");
			}

			// 3. Обработка и сжатие картинок
			await UploadMedia(originalDimensions, compressedDimensions, originalSizes, images, post);

			// 4. Сохраняем пост в БД
			await _postService.AddPostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType, botId });
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
			[FromForm] int? botId,
			[FromForm] List<string> keptImages, // Старые сохраненные картинки в Base64
			[FromForm] List<string> selectedNetworks, // Список выбранных соцсетей (для режима All)
			[FromForm] List<string> originalDimensions,   // Принимаем разрешение ДО
			[FromForm] List<string> compressedDimensions,
			[FromForm] List<long> originalSizes,
			[FromForm] List<IFormFile> images)   // Новые добавленные файлы
		{
			// 1. Получаем существующий пост из базы данных
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// 2. Обновляем базовые данные
			post.ShowDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			// 3. Обновление текстов и статусов соцсетей (Вызов общего хелпера)
			if (!FillNetworkData(post, networkType, selectedNetworks, caption, botId))
			{
				return BadRequest("Пожалуйста, выберите хотя бы одну социальную сеть для публикации.");
			}

			// 4. Обновляем список картинок поста
			post.Images = keptImages ?? new List<string>();

			// 5. Обработка новых картинок
			await UploadMedia(originalDimensions, compressedDimensions, originalSizes, images, post);

			// 6. Сохраняем изменения в базе
			await _postService.UpdatePostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType, botId });
		}

		private bool FillNetworkData(BlogPost post, string networkType, List<string> selectedNetworks, string caption, int? botId = null)
		{
			if (networkType == "All")
			{
				if (selectedNetworks == null || selectedNetworks.Count == 0)
				{
					return false; // Валидация не прошла
				}

				// Сбрасываем те сети, у которых сняли галочки
				foreach (var key in post.Networks.Keys.ToList())
				{
					if (!selectedNetworks.Contains(key))
					{
						post.Networks[key] = new NetworkPostData { Status = SocialStatus.None, Caption = "" };
					}
				}

				// Наполняем выбранные чекбоксами направления
				foreach (var netKey in selectedNetworks) // формат: "Instagram_5"
				{
					var parts = netKey.Split('_');
					if (Enum.TryParse<NetworkType>(parts[0], out var parsedNet))
					{
						var specificCaption = Request.Form[$"caption_{netKey}"].ToString();
						var finalCaption = string.IsNullOrEmpty(specificCaption) ? caption : specificCaption;

						if (post.Networks.ContainsKey(netKey))
						{
							post.Networks[netKey].Caption = finalCaption;
							if (post.Networks[netKey].Status == SocialStatus.None)
							{
								post.Networks[netKey].Status = SocialStatus.Pending;
							}
						}
						else
						{
							post.Networks[netKey] = new NetworkPostData
							{
								Status = SocialStatus.Pending,
								Caption = finalCaption
							};
						}
					}
				}
			}
			else
			{
				// ОДИНОЧНЫЙ РЕЖИМ (Instagram, Telegram...):
				if (Enum.TryParse<NetworkType>(networkType, out var parsedNet))
				{
					// Используем переданный botId напрямую!
					// Метод FindFirstActiveBotId вызовется только как фоллбек, если botId равен null
					var finalBotId = botId ?? FindFirstActiveBotId(post.ProfileId, parsedNet);
					var netKey = $"{networkType}_{finalBotId}";

					if (post.Networks.ContainsKey(netKey))
					{
						post.Networks[netKey].Caption = caption;

						if (post.Networks[netKey].Status == SocialStatus.None)
						{
							post.Networks[netKey].Status = SocialStatus.Pending;
						}
					}
					else
					{
						post.Networks[netKey] = new NetworkPostData
						{
							Status = SocialStatus.Pending,
							Caption = caption
						};
					}
				}
			}

			return true;
		}

		private async Task UploadMedia(List<string> originalDimensions, List<string> compressedDimensions, List<long> originalSizes, List<IFormFile> images, BlogPost post)
		{
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
					for (int i = 0; i < images.Count; i++)
					{
						var file = images[i];
						try
						{
							using var ms = new MemoryStream();
							await file.CopyToAsync(ms);
							var base64 = Convert.ToBase64String(ms.ToArray());
							post.Images.Add(base64);

							double receivedSizeMb = file.Length / (1024.0 * 1024.0);
							double dbSizeMb = base64.Length / (1024.0 * 1024.0);

							string origDim = (originalDimensions != null && originalDimensions.Count > i) ? originalDimensions[i] : "Неизвестно";
							string compDim = (compressedDimensions != null && compressedDimensions.Count > i) ? compressedDimensions[i] : "Неизвестно";

							long origSizeBytes = (originalSizes != null && originalSizes.Count > i) ? originalSizes[i] : 0;
							double origSizeMb = origSizeBytes / (1024.0 * 1024.0);

							_logger.LogInformation(
							"Файл [{FileName}] успешно получен от клиента:\n" +
							"  [РАЗРЕШЕНИЕ]: {OrigDim} px ==> уменьшено до ==> {CompDim} px\n" +
							"  [ВЕС ФАЙЛА]: Исходный: {OrigSize:F3} МБ ==> сжат до ==> {RecSize:F3} МБ\n" +
							"  [В БД (Base64)]: Длина строки {B64Length} символов | Примерный вес в БД: {DbSize:F3} МБ",
								file.FileName, origDim, compDim, origSizeMb, receivedSizeMb, base64.Length, dbSizeMb);
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при конвертации полученного файла {FileName}", file.FileName);
						}
					}
					_logger.LogInformation("============================================================");
				}
			}
		}

		[HttpPost("update-date/{id}")]
		public async Task<IActionResult> UpdateDate(Guid id, [FromForm] DateTime newDate)
		{
			// 1. Получаем существующий пост
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// 2. Обновляем только дату публикации (приводим ее к UTC)
			post.ShowDate = DateTime.SpecifyKind(newDate, DateTimeKind.Utc);

			// 3. Сохраняем изменения в БД и обновляем кеш
			await _postService.UpdatePostAsync(post);

			return Ok();
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
		public async Task<IActionResult> Delete(Guid id, [FromQuery] string networkType, [FromQuery] int? botId)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// Считаем количество активных направлений публикации
			var activeNets = post.Networks
				.Where(n => n.Value.Status != SocialStatus.None)
				.Select(n => n.Key)
				.ToList();

			// ЕСЛИ удаляем из общей вкладки "All" ИЛИ это была единственная активная сеть поста
			if (networkType == "All" || activeNets.Count <= 1)
			{
				// Удаляем полностью весь BlogPost из базы данных
				await _postService.DeletePostAsync(id);
			}
			else
			{
				// ЕСЛИ это мультипостинг, но удаляем из конкретной соцсети
				var netType = Enum.Parse<NetworkType>(networkType);
				var finalBotId = botId ?? FindFirstActiveBotId(post.ProfileId, netType);

				// Находим ключ вида "{Соцсеть}_{BotId}"
				var netKey = $"{networkType}_{finalBotId}";

				if (post.Networks.ContainsKey(netKey))
				{
					// Убираем только это направление (переводим в статус None)
					post.Networks[netKey] = new NetworkPostData { Status = SocialStatus.None, Caption = "" };
				}
				await _postService.UpdatePostAsync(post);
			}

			return Ok();
		}

		// Вспомогательный метод поиска первого активного BotId в профиле по типу сети
		private int FindFirstActiveBotId(int profileId, NetworkType netType)
		{
			var profile = _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.BlueSkySettingsList)
				.Include(p => p.TelegramChannelSettingsList)
				.FirstOrDefault(p => p.Id == profileId);

			if (profile == null) return 0;

			switch (netType)
			{
				case NetworkType.Instagram:
					return profile.InstagramSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.Facebook:
					return profile.FacebookSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.Threads:
					return profile.ThreadsSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.X:
					return profile.XSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.TelegramPublic:
					return profile.TelegramUserBotSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				case NetworkType.TelegramChannel:
					return profile.TelegramChannelSettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				//case NetworkType.TelegramP:
				//	return (profile.TelegramSettings != null && profile.TelegramSettings.IsActive) ? profile.TelegramSettings.UserId : 0;
				case NetworkType.BlueSky:
					return profile.BlueSkySettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				default:
					return 0;
			}
		}

	}
}
