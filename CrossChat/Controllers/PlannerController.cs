using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossChat.Data;
using CrossChat.Data.Emuns;
using CrossChat.Data.Entities;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Interfaces.Google;
using CrossChat.Integrations.Models;
using CrossChat.Integrations.Models.Posting;
using CrossChat.Integrations.Services;
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
		private readonly IGoogleDriveUploader _googleDriveUploader;
		private readonly ILogger<PlannerController> _logger;

		private const string GOOGLE_POSTS_FOLDER_ID = "1BCXzh7k4_eZM3bWVRy8BFmSx6y4fsigu";

		public PlannerController(
			AppDbContext db,
			IPostService postService,
			IGoogleDriveUploader googleDriveUploader,
			ILogger<PlannerController> logger)
		{
			_db = db;
			_postService = postService;
			_googleDriveUploader = googleDriveUploader;
			_logger = logger;
		}

		[HttpGet]
		public async Task<IActionResult> Index(int profileId, string network, int? botId)
		{
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

			ViewBag.ProfileId = profileId;
			ViewBag.Network = network;
			ViewBag.BotId = botId;

			return View(profile);
		}

		// ==========================================================
		// 4. GET EVENTS (ДОБАВЛЯЕМ ЗНАЧОК 🔁 ДЛЯ ПОВТОРЯЮЩИХСЯ ПОСТОВ)
		// ==========================================================
		[HttpGet("events")]
		public async Task<IActionResult> GetEvents(int profileId, string networkType, int? botId)
		{
			var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

			if (networkType == "All")
			{
				var posts = await _db.Posts
					.Include(p => p.NetworkStates)
					.Where(p => p.ProfileId == profileId &&
								p.NetworkStates.Any(ns => ns.Status != (int)SocialStatus.None))
					.ToListAsync();

				var events = posts.Select(p =>
				{
					var activeStates = p.NetworkStates.Where(ns => ns.Status != (int)SocialStatus.None).ToList();
					var mainCaption = activeStates.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Caption))?.Caption;
					if (string.IsNullOrWhiteSpace(mainCaption)) mainCaption = "Пост";

					// Если пост из серии повторений — добавляем красивый значок 🔁
					if (p.RecurrenceGroupId.HasValue)
					{
						mainCaption = "🔁 " + mainCaption;
					}

					string color = "#f59e0b"; // Оранжевый (Pending)
					if (activeStates.Any(ns => ns.Status == (int)SocialStatus.Error))
					{
						color = "#ef4444"; // Красный
					}
					else if (activeStates.All(ns => ns.Status == (int)SocialStatus.Published))
					{
						color = "#10b981"; // Зеленый
					}

					return new
					{
						id = p.Id,
						title = mainCaption,
						start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"),
						backgroundColor = color,
						network = "All",
						isRecurring = p.RecurrenceGroupId.HasValue,
						activeNetworks = activeStates.Select(ns => $"{((NetworkType)ns.NetworkType).ToString()}_{ns.BotId}").ToList()
					};
				});

				return Json(events);
			}
			else
			{
				var netType = Enum.Parse<NetworkType>(networkType);
				int netTypeId = (int)netType;
				var finalBotId = botId ?? FindFirstActiveBotId(profileId, netType);

				var posts = await _db.Posts
					.Include(p => p.NetworkStates)
					.Where(p => p.ProfileId == profileId &&
								p.NetworkStates.Any(ns => ns.NetworkType == netTypeId &&
														  ns.BotId == finalBotId &&
														  ns.Status != (int)SocialStatus.None))
					.ToListAsync();

				var events = posts.Select(p =>
				{
					var state = p.NetworkStates.FirstOrDefault(ns => ns.NetworkType == netTypeId && ns.BotId == finalBotId);
					var status = state?.Status ?? (int)SocialStatus.Pending;

					string color = status switch
					{
						(int)SocialStatus.Published => "#10b981",
						(int)SocialStatus.Error => "#ef4444",
						_ => "#f59e0b"
					};

					string title = string.IsNullOrWhiteSpace(state?.Caption) ? "Пост" : state.Caption;
					if (p.RecurrenceGroupId.HasValue)
					{
						title = "🔁 " + title;
					}

					return new
					{
						id = p.Id,
						title = title,
						start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"),
						backgroundColor = color,
						network = networkType,
						isRecurring = p.RecurrenceGroupId.HasValue
					};
				});

				return Json(events);
			}
		}

		// ==========================================================
		// 1. СОЗДАНИЕ ПОСТА (С ПОДДЕРЖКОЙ СЕРИИ ПОВТОРЕНИЙ)
		// ==========================================================
		[HttpPost("create")]
		[RequestSizeLimit(300 * 1024 * 1024)]
		[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
		public async Task<IActionResult> Create(
			[FromForm] int profileId,
			[FromForm] string networkType,
			[FromForm] List<string> selectedNetworks,
			[FromForm] string? caption,
			[FromForm] DateTime showDate,
			[FromForm] int? botId,
			// ПАРАМЕТРЫ ПОВТОРЕНИЯ:
			[FromForm] bool isRecurring,
			[FromForm] int recurrenceInterval,
			[FromForm] int recurrenceCount,
			List<IFormFile> images)
		{
			var utcDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			var post = new BlogPost
			{
				Id = Guid.NewGuid(),
				ProfileId = profileId,
				CreatedAt = DateTimeNow,
				ShowDate = utcDate,
				Access = AccessLevel.Public
			};

			if (!FillNetworkData(post, networkType, selectedNetworks, caption, botId))
			{
				return BadRequest("Пожалуйста, выберите хотя бы одну социальную сеть для публикации.");
			}

			// Загружаем файлы в Google Drive ОДИН РАЗ
			await UploadMedia(images, post);

			// ЕСЛИ ВКЛЮЧЕНО ПОВТОРЕНИЕ (ГЕНЕРАЦИЯ СЕРИИ):
			if (isRecurring && recurrenceCount > 1)
			{
				int interval = recurrenceInterval > 0 ? recurrenceInterval : 7;
				int count = Math.Clamp(recurrenceCount, 1, 24); // максимум 24 цикла
				var groupId = Guid.NewGuid();

				post.RecurrenceGroupId = groupId;
				await _postService.AddPostAsync(post);

				// Генерируем последующие посты серии
				for (int i = 1; i < count; i++)
				{
					var repeatPost = new BlogPost
					{
						Id = Guid.NewGuid(),
						ProfileId = profileId,
						CreatedAt = DateTimeNow,
						ShowDate = utcDate.AddDays(interval * i),
						Access = AccessLevel.Public,
						RecurrenceGroupId = groupId,
						// Ссылаемся на те же самые файлы в Google Диске (без дублирования места!)
						Media = post.Media.Select(m => new PostMediaItem
						{
							MediaType = m.MediaType,
							GoogleDriveFileId = m.GoogleDriveFileId,
							ThumbnailDriveFileId = m.ThumbnailDriveFileId,
							FileName = m.FileName,
							MimeType = m.MimeType,
							FileSizeBytes = m.FileSizeBytes,
							SortOrder = m.SortOrder
						}).ToList()
					};

					// Копируем настройки соцсетей (текст, кнопки, геолокацию, звезды)
					foreach (var kvp in post.Networks)
					{
						repeatPost.Networks[kvp.Key] = new NetworkPostData
						{
							Status = kvp.Value.Status,
							Caption = kvp.Value.Caption,
							FirstComment = kvp.Value.FirstComment,
							IsPaid = kvp.Value.IsPaid,
							Price = kvp.Value.Price,
							IsVideoNote = kvp.Value.IsVideoNote,
							ButtonText = kvp.Value.ButtonText,
							ButtonUrl = kvp.Value.ButtonUrl,
							LocationId = kvp.Value.LocationId,
							LocationName = kvp.Value.LocationName
						};
					}

					await _postService.AddPostAsync(repeatPost);
				}

				_logger.LogInformation("[Planner] Создана серия из {Count} повторяющихся постов с шагом {Days} дн. GroupId: {GroupId}", count, interval, groupId);
			}
			else
			{
				await _postService.AddPostAsync(post);
			}

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType, botId });
		}

		// ==========================================================
		// 2. ОБНОВЛЕНИЕ ПОСТА (ТОЛЬКО ЭТОТ ИЛИ ВСЯ СЕРИЯ)
		// ==========================================================
		[HttpPost("update/{id}")]
		[RequestSizeLimit(300 * 1024 * 1024)]
		[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
		public async Task<IActionResult> Update(
			Guid id,
			[FromForm] int profileId,
			[FromForm] string networkType,
			[FromForm] string? caption,
			[FromForm] DateTime showDate,
			[FromForm] int? botId,
			[FromForm] List<string> keptMediaDriveIds,
			[FromForm] List<string> selectedNetworks,
			// ФЛАГ: ПРИМЕНИТЬ КО ВСЕЙ СЕРИИ:
			[FromForm] bool updateSeries,
			List<IFormFile> images)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			post.ShowDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			if (!FillNetworkData(post, networkType, selectedNetworks, caption, botId))
			{
				return BadRequest("Пожалуйста, выберите хотя бы одну социальную сеть.");
			}

			// Безопасное удаление убранных медиа
			var keptSet = keptMediaDriveIds != null ? new HashSet<string>(keptMediaDriveIds) : new HashSet<string>();
			var removedMedia = post.Media.Where(m => !keptSet.Contains(m.GoogleDriveFileId)).ToList();

			foreach (var media in removedMedia)
			{
				await SafeDeleteMediaFileAsync(media.GoogleDriveFileId, excludingPostId: post.Id);
				if (!string.IsNullOrEmpty(media.ThumbnailDriveFileId))
				{
					await SafeDeleteMediaFileAsync(media.ThumbnailDriveFileId, excludingPostId: post.Id);
				}
			}

			post.Media = post.Media.Where(m => keptSet.Contains(m.GoogleDriveFileId)).ToList();
			await UploadMedia(images, post);

			// Обновляем текущий пост
			await _postService.UpdatePostAsync(post);

			// ЕСЛИ ПОЛЬЗОВАТЕЛЬ ВЫБРАЛ: "ПРИМЕНИТЬ КО ВСЕЙ СЕРИИ"
			if (post.RecurrenceGroupId.HasValue && updateSeries)
			{
				var futurePosts = await _db.Posts
					.Include(p => p.Media)
					.Include(p => p.NetworkStates)
					.Where(p => p.RecurrenceGroupId == post.RecurrenceGroupId.Value &&
								p.Id != post.Id &&
								p.ShowDate >= post.ShowDate)
					.ToListAsync();

				foreach (var fPost in futurePosts)
				{
					// Обновляем медиафайлы (ссылаемся на актуальный набор)
					fPost.Media.Clear();
					foreach (var m in post.Media)
					{
						fPost.Media.Add(new PostMediaEntity
						{
							PostId = fPost.Id,
							MediaType = m.MediaType,
							GoogleDriveFileId = m.GoogleDriveFileId,
							ThumbnailDriveFileId = m.ThumbnailDriveFileId,
							FileName = m.FileName,
							MimeType = m.MimeType,
							FileSizeBytes = m.FileSizeBytes,
							SortOrder = m.SortOrder
						});
					}

					// Обновляем тексты, кнопки, звезды, локацию
					foreach (var state in fPost.NetworkStates)
					{
						string netKey = $"{((NetworkType)state.NetworkType).ToString()}_{state.BotId}";
						if (post.Networks.TryGetValue(netKey, out var netData))
						{
							state.Caption = netData.Caption;
							state.FirstComment = netData.FirstComment;
							state.IsPaid = netData.IsPaid;
							state.Price = netData.Price;
							state.IsVideoNote = netData.IsVideoNote;
							state.ButtonText = netData.ButtonText;
							state.ButtonUrl = netData.ButtonUrl;
							state.LocationId = netData.LocationId;
							state.LocationName = netData.LocationName;
						}
					}
				}

				await _db.SaveChangesAsync();
				_logger.LogInformation("[Planner] Серия постов (GroupId: {GroupId}) успешно обновлена.", post.RecurrenceGroupId.Value);
			}

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType, botId });
		}

		private async Task UploadMedia(List<IFormFile> files, BlogPost post)
		{
			if (files == null || files.Count == 0) return;

			_logger.LogInformation("=== ЗАГРУЗКА МЕДИА В GOOGLE DRIVE ===");

			for (int i = 0; i < files.Count; i++)
			{
				var file = files[i];
				if (file.Length == 0) continue;

				try
				{
					var isVideo = file.ContentType.StartsWith("video/") ||
								  file.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
								  file.FileName.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

					var isAudio = file.ContentType.StartsWith("audio/") ||
								  file.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
								  file.FileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
								  file.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
								  file.FileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);

					var mediaType = isVideo ? MediaType.Video : (isAudio ? MediaType.Audio : MediaType.Image);

					string driveFileId;
					string? thumbDriveId = null;

					// ЕСЛИ ЭТО ВИДЕО — СОХРАНЯЕМ НА ДИСК ДЛЯ ГЕНЕРАЦИИ ОБЛОЖКИ (THUMBNAIL)
					if (isVideo)
					{
						string tempVideoPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{file.FileName}");

						try
						{
							// Записываем временный файл для быстрой работы FFmpeg
							using (var fs = new FileStream(tempVideoPath, FileMode.Create))
							{
								await file.CopyToAsync(fs);
							}

							// Загружаем видео в Google Drive
							using (var videoStream = new FileStream(tempVideoPath, FileMode.Open, FileAccess.Read))
							{
								driveFileId = await _googleDriveUploader.UploadStreamAsync(
									videoStream,
									file.FileName,
									GOOGLE_POSTS_FOLDER_ID,
									file.ContentType);
							}

							// Генерируем компактный кадр-обложку (20-30 КБ)
							string? thumbLocalPath = await VideoService.GenerateVideoThumbnailAsync(tempVideoPath);

							if (!string.IsNullOrEmpty(thumbLocalPath) && System.IO.File.Exists(thumbLocalPath))
							{
								try
								{
									using var thumbStream = new FileStream(thumbLocalPath, FileMode.Open, FileAccess.Read);
									string thumbName = $"thumb_{Path.GetFileNameWithoutExtension(file.FileName)}.jpg";

									thumbDriveId = await _googleDriveUploader.UploadStreamAsync(
										thumbStream,
										thumbName,
										GOOGLE_POSTS_FOLDER_ID,
										"image/jpeg");

									_logger.LogInformation("Обложка для видео успешно создана и загружена. ThumbId: {ThumbId}", thumbDriveId);
								}
								finally
								{
									try { System.IO.File.Delete(thumbLocalPath); } catch { }
								}
							}
						}
						finally
						{
							try { System.IO.File.Delete(tempVideoPath); } catch { }
						}
					}
					else
					{
						// Фото и аудио загружаем потоком напрямую в Google Drive
						using var stream = file.OpenReadStream();
						driveFileId = await _googleDriveUploader.UploadStreamAsync(
							stream,
							file.FileName,
							GOOGLE_POSTS_FOLDER_ID,
							file.ContentType);
					}

					post.Media.Add(new PostMediaItem
					{
						MediaType = mediaType,
						GoogleDriveFileId = driveFileId,
						ThumbnailDriveFileId = thumbDriveId, // Сохраняем ID легкой обложки!
						FileName = file.FileName,
						MimeType = file.ContentType,
						FileSizeBytes = file.Length,
						SortOrder = post.Media.Count
					});

					_logger.LogInformation("Файл {FileName} ({Size} байт) сохранен. DriveId: {DriveId}", file.FileName, file.Length, driveFileId);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Ошибка при сохранении медиафайла {FileName}", file.FileName);
				}
			}

			_logger.LogInformation("=========================================");
		}

		/// <summary>
		/// Эндпоинт для отображения превью фото и видео в календаре прямо из Google Drive
		/// </summary>
		[HttpGet("media/{driveFileId}")]
		public async Task<IActionResult> GetMediaFile(string driveFileId)
		{
			if (string.IsNullOrEmpty(driveFileId) || driveFileId == "undefined")
			{
				return BadRequest("Некорректный ID файла");
			}

			try
			{
				// Находим медиа в базе, чтобы отдать правильный MimeType (image/jpeg, video/mp4 и т.д.)
				var media = await _db.PostMedia.AsNoTracking().FirstOrDefaultAsync(m => m.GoogleDriveFileId == driveFileId);
				var contentType = !string.IsNullOrEmpty(media?.MimeType) ? media.MimeType : "image/jpeg";

				var stream = await _googleDriveUploader.GetFileStreamAsync(driveFileId);
				return File(stream, contentType, enableRangeProcessing: true);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Не удалось получить файл {DriveFileId} из Google Drive", driveFileId);
				return NotFound();
			}
		}

		[HttpPost("update-date/{id}")]
		public async Task<IActionResult> UpdateDate(Guid id, [FromForm] DateTime newDate)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			var utcDate = DateTime.SpecifyKind(newDate, DateTimeKind.Utc);
			post.ShowDate = utcDate;

			// ЕСЛИ ПОСТ ПЕРЕНЕСЕН В БУДУЩЕЕ: Сбрасываем упавшие сети из Error обратно в Pending!
			if (utcDate > DateTimeNow)
			{
				foreach (var net in post.Networks.Values)
				{
					if (net.Status == SocialStatus.Error)
					{
						net.Status = SocialStatus.Pending;
					}
				}
			}

			await _postService.UpdatePostAsync(post);
			return Ok();
		}

		[HttpGet("get/{id}")]
		public async Task<IActionResult> GetPost(Guid id)
		{
			var post = await _postService.GetPostByIdAsync(id);
			var options = new JsonSerializerOptions
			{
				Converters = { new JsonStringEnumConverter() },
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			};

			return post != null ? Json(post, options) : NotFound();
		}

		// ==========================================================
		// 3. УДАЛЕНИЕ ПОСТА (ТОЛЬКО ЭТОТ ИЛИ ВСЯ СЕРИЯ)
		// ==========================================================
		[HttpPost("delete/{id}")]
		public async Task<IActionResult> Delete(
			Guid id,
			[FromQuery] string networkType,
			[FromQuery] int? botId,
			[FromQuery] bool deleteSeries = false)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			// СЦЕНАРИЙ А: УДАЛЕНИЕ ВСЕЙ СЕРИИ
			if (post.RecurrenceGroupId.HasValue && deleteSeries)
			{
				var seriesPosts = await _db.Posts
					.Include(p => p.Media)
					.Where(p => p.RecurrenceGroupId == post.RecurrenceGroupId.Value && p.ShowDate >= post.ShowDate)
					.ToListAsync();

				var postIds = seriesPosts.Select(p => p.Id).ToList();

				// Удаляем медиафайлы только если они не используются другими постами вне этой серии
				var driveIds = seriesPosts.SelectMany(p => p.Media).Select(m => m.GoogleDriveFileId).Distinct().ToList();
				foreach (var dId in driveIds)
				{
					await SafeDeleteMediaFileAsync(dId, excludingPostIds: postIds);
				}

				foreach (var p in seriesPosts)
				{
					await _postService.DeletePostAsync(p.Id);
				}

				_logger.LogInformation("[Planner] Удалена вся серия постов (GroupId: {GroupId}) начиная с {Date}", post.RecurrenceGroupId.Value, post.ShowDate);
			}
			// СЦЕНАРИЙ Б: УДАЛЕНИЕ ТОЛЬКО ЭТОГО ПОСТА
			else
			{
				var activeNets = post.Networks.Where(n => n.Value.Status != SocialStatus.None).Select(n => n.Key).ToList();

				if (networkType == "All" || activeNets.Count <= 1)
				{
					// Безопасное удаление из Google Drive с защитой файлов серии
					foreach (var media in post.Media)
					{
						await SafeDeleteMediaFileAsync(media.GoogleDriveFileId, excludingPostId: post.Id);
						if (!string.IsNullOrEmpty(media.ThumbnailDriveFileId))
						{
							await SafeDeleteMediaFileAsync(media.ThumbnailDriveFileId, excludingPostId: post.Id);
						}
					}

					await _postService.DeletePostAsync(id);
				}
				else
				{
					var netType = Enum.Parse<NetworkType>(networkType);
					var finalBotId = botId ?? FindFirstActiveBotId(post.ProfileId, netType);
					var netKey = $"{networkType}_{finalBotId}";

					if (post.Networks.ContainsKey(netKey))
					{
						post.Networks[netKey] = new NetworkPostData { Status = SocialStatus.None, Caption = "" };
					}
					await _postService.UpdatePostAsync(post);
				}
			}

			return Ok();
		}


		// ==========================================================
		// ВСПОМОГАТЕЛЬНЫЙ МЕТОД: БЕЗОПАСНОЕ УДАЛЕНИЕ ИЗ GOOGLE DRIVE
		// (Удаляет файл из облака ТОЛЬКО если на него больше никто не ссылается!)
		// ==========================================================
		private async Task SafeDeleteMediaFileAsync(string? driveFileId, Guid? excludingPostId = null, List<Guid>? excludingPostIds = null)
		{
			if (string.IsNullOrEmpty(driveFileId)) return;

			try
			{
				var query = _db.PostMedia.AsNoTracking().Where(m => m.GoogleDriveFileId == driveFileId);

				if (excludingPostId.HasValue)
					query = query.Where(m => m.PostId != excludingPostId.Value);

				if (excludingPostIds != null && excludingPostIds.Any())
					query = query.Where(m => !excludingPostIds.Contains(m.PostId));

				bool isStillUsed = await query.AnyAsync();

				if (!isStillUsed)
				{
					await _googleDriveUploader.DeleteFileByIdAsync(driveFileId);
					_logger.LogInformation("[Storage] Файл {DriveId} удален из Google Drive (нет ссылок).", driveFileId);
				}
				else
				{
					_logger.LogInformation("[Storage] Файл {DriveId} сохранен в Google Drive (используется другими постами серии).", driveFileId);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "[Storage] Ошибка проверки использования файла {DriveId}", driveFileId);
			}
		}


		// ==========================================================
		// ПОИСК И АВТО-КЭШИРОВАНИЕ ГЕОЛОКАЦИЙ (META PLACES)
		// ==========================================================
		[HttpGet("locations/search")]
		public async Task<IActionResult> SearchLocations([FromQuery] string q)
		{
			// Проверяем / перезаполняем стартовый справочник
			if (!await _db.SavedLocations.AnyAsync())
			{
				var verifiedLocations = new List<SavedLocation>
				{
					// Проверенный в бою Нью-Йорк (Музей Гуггенхайма)
					new() { LocationId = "7640348500", Name = "Solomon R. Guggenheim Museum (New York, USA)" },
				};

				_db.SavedLocations.AddRange(verifiedLocations);
				await _db.SaveChangesAsync();
			}

			if (string.IsNullOrWhiteSpace(q))
			{
				var topList = await _db.SavedLocations
					.OrderByDescending(l => l.CreatedAt)
					.Take(12)
					.Select(l => new { id = l.LocationId, name = l.Name })
					.ToListAsync();
				return Json(topList);
			}

			string cleanQuery = q.Trim().ToLowerInvariant();

			var matches = await _db.SavedLocations
				.Where(l => l.Name.ToLower().Contains(cleanQuery))
				.OrderByDescending(l => l.CreatedAt)
				.Take(12)
				.Select(l => new { id = l.LocationId, name = l.Name })
				.ToListAsync();

			return Json(matches);
		}

		/// <summary>
		/// Добавление своего места вручную по ID Meta
		/// </summary>
		[HttpPost("locations/add-custom")]
		public async Task<IActionResult> AddCustomLocation([FromForm] string locationId, [FromForm] string name)
		{
			if (string.IsNullOrWhiteSpace(locationId) || string.IsNullOrWhiteSpace(name))
				return BadRequest("ID и название обязательны");

			locationId = locationId.Trim();
			name = name.Trim();

			if (!await _db.SavedLocations.AnyAsync(l => l.LocationId == locationId))
			{
				_db.SavedLocations.Add(new SavedLocation
				{
					LocationId = locationId,
					Name = name,
					CreatedAt = DateTime.UtcNow
				});
				await _db.SaveChangesAsync();
			}

			return Json(new { success = true, id = locationId, name = name });
		}

		private bool FillNetworkData(BlogPost post, string networkType, List<string> selectedNetworks, string caption, int? botId = null)
		{
			// 1. ЗАЩИТА: Если текст не ввели, заменяем null на пустую строку ""
			var safeCaption = caption ?? string.Empty;

			bool isVideoNoteTelegram = Request.Form["isVideoNoteTelegram"] == "true";
			string? tgButtonText = Request.Form["tgButtonText"].FirstOrDefault();
			string? tgButtonUrl = Request.Form["tgButtonUrl"].FirstOrDefault();
			string? firstComment = Request.Form["firstComment"].ToString();
			if (string.IsNullOrWhiteSpace(firstComment)) firstComment = null;

			// Страховка: если строка уже пришла с запятыми из-за дубля полей формы — берем первую часть
			if (!string.IsNullOrEmpty(tgButtonText) && tgButtonText.Contains(","))
				tgButtonText = tgButtonText.Split(',')[0].Trim();

			if (!string.IsNullOrEmpty(tgButtonUrl) && tgButtonUrl.Contains(","))
				tgButtonUrl = tgButtonUrl.Split(',')[0].Trim();

			string? locationId = Request.Form["locationId"].ToString();
			string? locationName = Request.Form["locationName"].ToString();
			if (string.IsNullOrWhiteSpace(locationId)) { locationId = null; locationName = null; }

			// Читаем параметры платного поста для Telegram
			bool isPaidTelegram = Request.Form["isPaidTelegram"] == "true";
			int.TryParse(Request.Form["priceTelegram"], out int priceTelegram);
			if (priceTelegram <= 0) priceTelegram = 50;

			// Флаг: одинаковые ли настройки звезд для всех каналов (по умолчанию true)
			bool isUnifiedTelegramPaid = Request.Form["isUnifiedTelegramPaid"] != "false";
			// Флаг единого режима для кнопок-ссылок
			bool isUnifiedTelegramButton = Request.Form["isUnifiedTelegramButton"].FirstOrDefault() != "false";

			if (networkType == "All")
			{
				if (selectedNetworks == null || selectedNetworks.Count == 0)
				{
					return false;
				}

				foreach (var key in post.Networks.Keys.ToList())
				{
					if (!selectedNetworks.Contains(key))
					{
						post.Networks[key] = new NetworkPostData { Status = SocialStatus.None, Caption = "" };
					}
				}

				foreach (var netKey in selectedNetworks)
				{
					var parts = netKey.Split('_');
					if (Enum.TryParse<NetworkType>(parts[0], out var parsedNet))
					{
						var specificCaption = Request.Form[$"caption_{netKey}"].ToString();

						// Защита от null для раздельных текстов
						var finalCaption = string.IsNullOrEmpty(specificCaption) ? safeCaption : specificCaption;
						finalCaption ??= string.Empty;

						bool isTg = parsedNet == NetworkType.TelegramChannel || parsedNet == NetworkType.TelegramPublic;
						bool isInstaOrFb = parsedNet == NetworkType.Instagram || parsedNet == NetworkType.Facebook;

						// РАСЧЕТ ЗВЕЗД ДЛЯ КОНКРЕТНОГО КАНАЛА:
						bool channelIsPaid = isPaidTelegram;
						int channelPrice = priceTelegram;

						if (isTg && !isUnifiedTelegramPaid)
						{
							// Если включен РАЗДЕЛЬНЫЙ режим, берем индивидуальные параметры этого канала:
							channelIsPaid = Request.Form[$"isPaid_{netKey}"] == "true";
							if (int.TryParse(Request.Form[$"price_{netKey}"], out int p) && p > 0)
							{
								channelPrice = Math.Clamp(p, 1, 2500);
							}
							else
							{
								channelPrice = 50;
							}
						}

						if (post.Networks.ContainsKey(netKey))
						{
							post.Networks[netKey].Caption = finalCaption;
							post.Networks[netKey].FirstComment = isTg ? null : firstComment;


							if (isTg)
							{
								// РАСЧЕТ КНОПКИ ДЛЯ КОНКРЕТНОГО КАНАЛА:
								string? channelButtonText = tgButtonText;
								string? channelButtonUrl = tgButtonUrl;

								if (!isUnifiedTelegramButton)
								{
									// Если включен РАЗДЕЛЬНЫЙ режим, считываем индивидуальные поля этого канала:
									channelButtonText = Request.Form[$"tgButtonText_{netKey}"].FirstOrDefault();
									channelButtonUrl = Request.Form[$"tgButtonUrl_{netKey}"].FirstOrDefault();

									 if (!string.IsNullOrEmpty(channelButtonText) && channelButtonText.Contains(",")) 
										channelButtonText = channelButtonText.Split(',')[0].Trim();

									if (!string.IsNullOrEmpty(channelButtonUrl) && channelButtonUrl.Contains(",")) 
										channelButtonUrl = channelButtonUrl.Split(',')[0].Trim();
								}

								post.Networks[netKey].IsPaid = channelIsPaid;
								post.Networks[netKey].Price = channelIsPaid ? channelPrice : 0;
								post.Networks[netKey].IsVideoNote = isVideoNoteTelegram;
								post.Networks[netKey].ButtonText = string.IsNullOrWhiteSpace(channelButtonText) ? null : channelButtonText.Trim();
								post.Networks[netKey].ButtonUrl = string.IsNullOrWhiteSpace(channelButtonUrl) ? null : channelButtonUrl.Trim();
							}

							if (isInstaOrFb)
							{
								post.Networks[netKey].LocationId = locationId;
								post.Networks[netKey].LocationName = locationName;
							}

							// ЕСЛИ БЫЛ В ОШИБКЕ ИЛИ НОВЫЙ — СБРАСЫВАЕМ В PENDING!
							if (post.Networks[netKey].Status == SocialStatus.None || post.Networks[netKey].Status == SocialStatus.Error)
							{
								post.Networks[netKey].Status = SocialStatus.Pending;
							}
						}
						else
						{
							post.Networks[netKey] = new NetworkPostData
							{
								Status = SocialStatus.Pending,
								Caption = finalCaption,
								FirstComment = isTg ? null : firstComment,
								IsPaid = isTg && channelIsPaid,
								Price = (isTg && channelIsPaid) ? channelPrice : 0,
								IsVideoNote = isTg && isVideoNoteTelegram,
								ButtonText = isTg && !string.IsNullOrWhiteSpace(tgButtonText) ? tgButtonText.Trim() : null,
								ButtonUrl = isTg && !string.IsNullOrWhiteSpace(tgButtonUrl) ? tgButtonUrl.Trim() : null,
								LocationId = isInstaOrFb ? locationId : null,
								LocationName = isInstaOrFb ? locationName : null,
							};
						}
					}
				}
			}
			else
			{
				if (Enum.TryParse<NetworkType>(networkType, out var parsedNet))
				{
					var finalBotId = botId ?? FindFirstActiveBotId(post.ProfileId, parsedNet);
					var netKey = $"{networkType}_{finalBotId}";

					bool isTg = parsedNet == NetworkType.TelegramChannel || parsedNet == NetworkType.TelegramPublic;
					bool isCurrentInstaOrFb = parsedNet == NetworkType.Instagram || parsedNet == NetworkType.Facebook;

					if (post.Networks.ContainsKey(netKey))
					{
						post.Networks[netKey].Caption = safeCaption;
						post.Networks[netKey].FirstComment = isTg ? null : firstComment;
						if (isTg)
						{
							post.Networks[netKey].IsPaid = isPaidTelegram;
							post.Networks[netKey].Price = isPaidTelegram ? priceTelegram : 0;
							post.Networks[netKey].IsVideoNote = isVideoNoteTelegram;
							post.Networks[netKey].ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim();
							post.Networks[netKey].ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim();
						}

						if (isCurrentInstaOrFb)
						{
							post.Networks[netKey].LocationId = locationId;
							post.Networks[netKey].LocationName = locationName;
						}

						// ЕСЛИ БЫЛ В ОШИБКЕ ИЛИ НОВЫЙ — СБРАСЫВАЕМ В PENDING!
						if (post.Networks[netKey].Status == SocialStatus.None || post.Networks[netKey].Status == SocialStatus.Error)
						{
							post.Networks[netKey].Status = SocialStatus.Pending;
						}
					}
					else
					{
						post.Networks[netKey] = new NetworkPostData
						{
							Status = SocialStatus.Pending,
							Caption = safeCaption, // <-- Используем safeCaption
							IsPaid = isTg && isPaidTelegram,
							Price = (isTg && isPaidTelegram) ? priceTelegram : 0,
							IsVideoNote = isVideoNoteTelegram,
							ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim(),
							ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim(),
							FirstComment = isTg ? null : firstComment,
							LocationId = isCurrentInstaOrFb ? locationId : null,
							LocationName = isCurrentInstaOrFb ? locationName : null,
						};
					}
				}
			}

			return true;
		}

		private int FindFirstActiveBotId(int profileId, NetworkType netType)
		{
			var profile = _db.Profile
				.Include(p => p.InstagramSettingsList)
				.Include(p => p.FacebookSettingsList)
				.Include(p => p.ThreadsSettingsList)
				.Include(p => p.XSettingsList)
				.Include(p => p.TelegramUserBotSettingsList)
				.Include(p => p.TelegramChannelSettingsList)
				.Include(p => p.TelegramSettings)
				.Include(p => p.BlueSkySettingsList)
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
				case NetworkType.BlueSky:
					return profile.BlueSkySettingsList.FirstOrDefault(x => x.IsActive)?.Id ?? 0;
				default:
					return 0;
			}
		}
	}
}