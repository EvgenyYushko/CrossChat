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

			await _console.Log($"Начало отправки поста в профиль {settings.PageName}.", settings.UserId, state.BotId);

			await _service.PublishToPageAsync(caption, settings.PageAccessToken, settings.PageId, images);
			//await FaceBookStory(files, fbSettings.PageAccessToken, fbSettings.PageId);

			await _console.Log($"Пост успешно опубликован в профиль {settings.PageName}.", settings.UserId, state.BotId);
		}
	}
}
