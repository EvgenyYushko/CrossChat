using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Worker.Publishers
{
	public class FacebookPublisher : ISocialPublisher
	{
		public NetworkType Network => NetworkType.Facebook;

		private readonly AppDbContext _db;
		private readonly IFaceBookService _service;
		private readonly IFaceBookConsole _console;

		public FacebookPublisher(AppDbContext db, IFaceBookService service, IFaceBookConsole console)
		{
			_db = db;
			_service = service;
			_console = console;
		}

		public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
		{
			var settings = await _db.FacebookSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
			if (settings == null || string.IsNullOrEmpty(settings.PageAccessToken))
				throw new Exception($"Не найдены настройки или PageAccessToken для Facebook (BotId: {state.BotId})");

			await _console.Log($"Начало отправки поста в страницу {settings.PageName}.", settings.UserId, state.BotId);

			bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
			var videoItem = images?.FirstOrDefault(isVideo);
			var photoItems = images?.Where(s => !isVideo(s)).ToList() ?? new List<string>();

			bool postSuccess = false;
			string? publishedPostId = null;

			// 1. ВИДЕО (REELS)
			if (videoItem != null)
			{
				await _console.Log("Обнаружено видео. Публикация Facebook Reel...", settings.UserId, state.BotId);
				var result = await _service.PublishReelAsync(caption, videoItem, settings.PageAccessToken, settings.PageId);
				postSuccess = result.Success;
				publishedPostId = result.PostId;
			}
			// 2. ФОТО / АЛЬБОМ
			else if (photoItems.Any())
			{
				await _console.Log($"Публикация {photoItems.Count} фото в ленту страницы Facebook...", settings.UserId, state.BotId);
				var result = await _service.PublishToPageAsync(caption, settings.PageAccessToken, settings.PageId, photoItems);
				postSuccess = result.Success;
				publishedPostId = result.PostId;
			}
			// 3. ТЕКСТ
			else
			{
				await _console.Log("Публикация текстового поста в Facebook...", settings.UserId, state.BotId);
				var result = await _service.PublishToPageAsync(caption, settings.PageAccessToken, settings.PageId, null);
				postSuccess = result.Success;
				publishedPostId = result.PostId;
			}

			if (!postSuccess)
			{
				throw new Exception($"Ошибка при публикации основного поста на страницу Facebook: {settings.PageName}");
			}

			await _console.Log($"Основной пост успешно опубликован на странице {settings.PageName}.", settings.UserId, state.BotId);

			// === 4. ПУБЛИКАЦИЯ ИСТОРИИ (STORY) ===
			if (photoItems.Any())
			{
				try
				{
					var firstPhoto = photoItems.First();
					await _console.Log("Публикация первого фото в истории страницы Facebook...", settings.UserId, state.BotId);
					bool storySuccess = await _service.PublishStoryAsync(firstPhoto, settings.PageAccessToken, settings.PageId);
					if (storySuccess)
					{
						await _console.Log("История Facebook успешно опубликована!", settings.UserId, state.BotId);
					}
					else
					{
						await _console.Log("⚠️ Не удалось опубликовать историю Facebook (основной пост опубликован).", settings.UserId, state.BotId);
					}
				}
				catch (Exception ex)
				{
					await _console.Log($"⚠️ Ошибка при создании истории Facebook: {ex.Message}", settings.UserId, state.BotId);
				}
			}

			// === 5. [НОВОЕ] ПУБЛИКАЦИЯ ПЕРВОГО КОММЕНТАРИЯ ===
			if (!string.IsNullOrWhiteSpace(state.FirstComment) && !string.IsNullOrEmpty(publishedPostId))
			{
				try
				{
					await _console.Log("Публикация первого комментария к публикации Facebook...", settings.UserId, state.BotId);

					// Небольшая задержка 2 сек для индексации поста в ленте Facebook
					await Task.Delay(2000);

					var commentId = await _service.CreateCommentAsync(publishedPostId, state.FirstComment.Trim(), settings.PageAccessToken);
					if (!string.IsNullOrEmpty(commentId))
					{
						await _console.Log("Первый комментарий в Facebook успешно опубликован!", settings.UserId, state.BotId);
					}
					else
					{
						await _console.Log("⚠️ Не удалось опубликовать первый комментарий в Facebook (основной пост опубликован).", settings.UserId, state.BotId);
					}
				}
				catch (Exception ex)
				{
					await _console.Log($"⚠️ Ошибка при создании первого комментария в Facebook: {ex.Message}", settings.UserId, state.BotId);
				}
			}
		}
	}
}