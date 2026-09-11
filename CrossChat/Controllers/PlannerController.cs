using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossChat.Data;
using CrossChat.Data.Emuns;
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
					var mainCaption = activeStates.FirstOrDefault()?.Caption ?? "Пост";

					return new
					{
						id = p.Id,
						title = mainCaption,
						start = p.ShowDate.ToString("yyyy-MM-ddTHH:mm:ss"),
						backgroundColor = "#4f46e5",
						network = "All",
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
		[RequestSizeLimit(300 * 1024 * 1024)] // Лимит 300 МБ для поддержки видео
		[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
		public async Task<IActionResult> Create(
			[FromForm] int profileId,
			[FromForm] string networkType,
			[FromForm] List<string> selectedNetworks,
			[FromForm] string caption,
			[FromForm] DateTime showDate,
			[FromForm] int? botId,
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

			// Загрузка медиа в Google Drive
			await UploadMedia(images, post);

			await _postService.AddPostAsync(post);

			return RedirectToAction("Index", "Planner", new { profileId, network = networkType, botId });
		}

		[HttpPost("update/{id}")]
		[RequestSizeLimit(300 * 1024 * 1024)]
		[RequestFormLimits(MultipartBodyLengthLimit = 300 * 1024 * 1024)]
		public async Task<IActionResult> Update(
			Guid id,
			[FromForm] int profileId,
			[FromForm] string networkType,
			[FromForm] string caption,
			[FromForm] DateTime showDate,
			[FromForm] int? botId,
			[FromForm] List<string> keptMediaDriveIds, // ID файлов, которые пользователь оставил
			[FromForm] List<string> selectedNetworks,
			[FromForm] List<IFormFile> images)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			post.ShowDate = DateTime.SpecifyKind(showDate, DateTimeKind.Utc);

			if (!FillNetworkData(post, networkType, selectedNetworks, caption, botId))
			{
				return BadRequest("Пожалуйста, выберите хотя бы одну социальную сеть для публикации.");
			}

			// === УДАЛЕНИЕ ИЗ GOOGLE DRIVE ТЕХ ФАЙЛОВ, КОТОРЫЕ УДАЛИЛИ ПО КРЕСТИКУ ===
			var keptSet = keptMediaDriveIds != null
				? new HashSet<string>(keptMediaDriveIds)
				: new HashSet<string>();

			// Находим те медиа, которых нет в списке оставленных (пользователь нажал на них крестик)
			var removedMedia = post.Media
				.Where(m => !keptSet.Contains(m.GoogleDriveFileId))
				.ToList();

			// Удаляем каждый удаленный файл из Google Диска
			foreach (var media in removedMedia)
			{
				_logger.LogInformation("Удаление файла {FileName} (DriveId: {DriveId}) из Google Drive...",
					media.FileName, media.GoogleDriveFileId);

				await _googleDriveUploader.DeleteFileByIdAsync(media.GoogleDriveFileId);

				// Если у файла была отдельная превьюшка, удаляем и её
				if (!string.IsNullOrEmpty(media.ThumbnailDriveFileId))
				{
					await _googleDriveUploader.DeleteFileByIdAsync(media.ThumbnailDriveFileId);
				}
			}

			// Оставляем в посте только те файлы, которые остались активными
			post.Media = post.Media.Where(m => keptSet.Contains(m.GoogleDriveFileId)).ToList();

			// Догружаем новые выбранные медиафайлы в Google Drive
			await UploadMedia(images, post);

			// Сохраняем изменения в базе данных
			await _postService.UpdatePostAsync(post);

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

			post.ShowDate = DateTime.SpecifyKind(newDate, DateTimeKind.Utc);
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

		[HttpPost("delete/{id}")]
		public async Task<IActionResult> Delete(Guid id, [FromQuery] string networkType, [FromQuery] int? botId)
		{
			var post = await _postService.GetPostByIdAsync(id);
			if (post == null) return NotFound();

			var activeNets = post.Networks
				.Where(n => n.Value.Status != SocialStatus.None)
				.Select(n => n.Key)
				.ToList();

			if (networkType == "All" || activeNets.Count <= 1)
			{
				// Удаляем файлы из Google Drive перед удалением поста
				foreach (var media in post.Media)
				{
					await _googleDriveUploader.DeleteFileByIdAsync(media.GoogleDriveFileId);
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

			return Ok();
		}

		private bool FillNetworkData(BlogPost post, string networkType, List<string> selectedNetworks, string caption, int? botId = null)
		{
			// 1. ЗАЩИТА: Если текст не ввели, заменяем null на пустую строку ""
			var safeCaption = caption ?? string.Empty;

			// Читаем параметры платного поста для Telegram
			bool isPaidTelegram = Request.Form["isPaidTelegram"] == "true";
			int.TryParse(Request.Form["priceTelegram"], out int priceTelegram);
			bool isVideoNoteTelegram = Request.Form["isVideoNoteTelegram"] == "true";
			string? tgButtonText = Request.Form["tgButtonText"].ToString();
			string? tgButtonUrl = Request.Form["tgButtonUrl"].ToString();
			if (priceTelegram <= 0) priceTelegram = 50;

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

						if (post.Networks.ContainsKey(netKey))
						{
							post.Networks[netKey].Caption = finalCaption;
							if (isTg)
							{
								post.Networks[netKey].IsPaid = isPaidTelegram;
								post.Networks[netKey].Price = isPaidTelegram ? priceTelegram : 0;
								post.Networks[netKey].IsVideoNote = isVideoNoteTelegram;
								post.Networks[netKey].ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim();
								post.Networks[netKey].ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim();
							}
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
								Caption = finalCaption,
								IsPaid = isTg && isPaidTelegram,
								Price = (isTg && isPaidTelegram) ? priceTelegram : 0,
								IsVideoNote = isVideoNoteTelegram,
								ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim(),
								ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim()
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

					if (post.Networks.ContainsKey(netKey))
					{
						post.Networks[netKey].Caption = safeCaption; // <-- Используем safeCaption
						if (isTg)
						{
							post.Networks[netKey].IsPaid = isPaidTelegram;
							post.Networks[netKey].Price = isPaidTelegram ? priceTelegram : 0;
							post.Networks[netKey].IsVideoNote = isVideoNoteTelegram;
							post.Networks[netKey].ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim();
							post.Networks[netKey].ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim();
						}

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
							Caption = safeCaption, // <-- Используем safeCaption
							IsPaid = isTg && isPaidTelegram,
							Price = (isTg && isPaidTelegram) ? priceTelegram : 0,
							IsVideoNote = isVideoNoteTelegram,
							ButtonText = string.IsNullOrWhiteSpace(tgButtonText) ? null : tgButtonText.Trim(),
							ButtonUrl = string.IsNullOrWhiteSpace(tgButtonUrl) ? null : tgButtonUrl.Trim()
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