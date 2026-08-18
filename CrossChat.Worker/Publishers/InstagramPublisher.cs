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

			var result = await _service.CreateMediaAsync(images, settings.AccessToken, caption);
			if (!result.Success)
				throw new Exception($"Ошибка API при публикации поста в Instagram (BotId: {state.BotId})");

			await _console.Log($"Пост успешно опубликован в профиль {settings.Username}.", settings.UserId, state.BotId);

			// 3. Публикуем историю (если есть картинки)
			//if (files != null && files.Any())
			//{
			//	try
			//	{
			//		string? storyId = await InstagramStory(files, instaSettings.AccessToken);
			//		if (storyId is not null)
			//		{
			//			_logger.LogInformation("✅ Instagram Story успешно опубликована (StoryId: {StoryId})", storyId);
			//		}
			//	}
			//	catch (Exception ex)
			//	{
			//		_logger.LogError(ex, "Ошибка при отправке Instagram Story для бота {BotId}", state.BotId);
			//	}
			//}
		}
	}
}
