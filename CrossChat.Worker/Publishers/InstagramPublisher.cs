using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Worker.Publishers
{
	public class InstagramPublisher : ISocialPublisher
	{
		public NetworkType Network => NetworkType.Instagram;

		private readonly AppDbContext _db;
		private readonly IInstagramService _service;
		private readonly IInstagramConsole _console;

		public InstagramPublisher(AppDbContext db, IInstagramService service, IInstagramConsole console)
		{
			_db = db;
			_service = service;
			_console = console;
		}

		public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
		{
			var settings = await _db.InstagramSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
			if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
				throw new Exception($"Не найдены настройки или AccessToken для Instagram (BotId: {state.BotId})");

			await _console.Log($"Начало отправки поста в профиль {settings.Username}.", settings.UserId, state.BotId);

			// 1. Публикуем основной пост
			var result = await _service.CreateMediaAsync(images, settings.AccessToken, caption, state.LocationId);
			if (!result.Success || string.IsNullOrEmpty(result.Id))
				throw new Exception($"Ошибка API при публикации поста в Instagram (BotId: {state.BotId})");

			await _console.Log($"Пост успешно опубликован в профиль {settings.Username}.", settings.UserId, state.BotId);

			// 2. ПУБЛИКАЦИЯ ПЕРВОГО КОММЕНТАРИЯ (если задан)
			if (!string.IsNullOrWhiteSpace(state.FirstComment))
			{
				try
				{
					await _console.Log("Публикация первого комментария к посту в Instagram...", settings.UserId, state.BotId);

					// Небольшая задержка, чтобы пост проиндексировался серверами Meta
					await Task.Delay(2000);

					var commentId = await _service.CreateCommentAsync(result.Id, state.FirstComment.Trim(), settings.AccessToken);
					if (!string.IsNullOrEmpty(commentId))
					{
						await _console.Log("Первый комментарий в Instagram успешно опубликован!", settings.UserId, state.BotId);
					}
					else
					{
						await _console.Log("⚠️ Не удалось опубликовать первый комментарий (основной пост при этом опубликован).", settings.UserId, state.BotId);
					}
				}
				catch (Exception ex)
				{
					await _console.Log($"⚠️ Ошибка при отправке первого комментария: {ex.Message}", settings.UserId, state.BotId);
				}
			}
		}
	}
}
