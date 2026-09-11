using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class XPublisher : ISocialPublisher
{
	public NetworkType Network => NetworkType.X;

	private readonly AppDbContext _db;
	private readonly IXService _service;
	private readonly IXConsole _console;

	public XPublisher(AppDbContext db, IXService service, IXConsole console)
	{
		_db = db;
		_service = service;
		_console = console;
	}

	public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
	{
		var settings = await _db.XSettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
		if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			throw new Exception($"Не найдены настройки для X (Twitter) (BotId: {state.BotId})");

		await _console.Log($"Начало отправки поста в X @{settings.ScreenName}.", settings.UserId, state.BotId);

		// Определяем типы файлов: видео или фото
		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
		var videoItem = images?.FirstOrDefault(isVideo);
		var photoItems = images?.Where(s => !isVideo(s)).ToList() ?? new List<string>();

		bool success;

		// 1. ВИДЕО
		if (videoItem != null)
		{
			await _console.Log("Обнаружено видео. Загрузка ролика в X...", settings.UserId, state.BotId);
			success = await _service.CreateVideoPost(caption, videoItem, settings.AccessToken);
		}
		// 2. ФОТО (до 4 картинок)
		else if (photoItems.Any())
		{
			await _console.Log($"Публикация твита с {photoItems.Count} фото в X...", settings.UserId, state.BotId);
			success = await _service.CreateImagePost(caption, photoItems, settings.AccessToken);
		}
		// 3. ТЕКСТОВЫЙ ТВИТ
		else
		{
			await _console.Log("Публикация текстового твита в X...", settings.UserId, state.BotId);
			success = await _service.CreateTextPostAsync(caption, settings.AccessToken);
		}

		if (!success)
			throw new Exception($"Ошибка при публикации твита в X @{settings.ScreenName}");

		await _console.Log($"Пост успешно опубликован в X @{settings.ScreenName}.", settings.UserId, state.BotId);
	}
}