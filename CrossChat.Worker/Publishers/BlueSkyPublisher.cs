using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class BlueSkyPublisher : ISocialPublisher
{
	public NetworkType Network => NetworkType.BlueSky;

	private readonly AppDbContext _db;
	private readonly IBlueSkyService _service;
	private readonly IBlueSkyConsole _console;

	public BlueSkyPublisher(AppDbContext db, IBlueSkyService service, IBlueSkyConsole console)
	{
		_db = db;
		_service = service;
		_console = console;
	}

	public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
	{
		var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
		if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			throw new Exception($"Не найдены настройки для BlueSky (BotId: {state.BotId})");

		await _console.Log($"Начало отправки поста в BlueSky @{settings.Handle}.", settings.UserId, state.BotId);

		var botModel = new BlueSkyModel
		{
			AccessToken = settings.AccessToken,
			RefreshToken = settings.RefreshToken,
			Handle = settings.Handle,
			PrivateKeyJson = settings.PrivateKeyJson,
			TokenExpiresAt = settings.TokenExpiresAt,
			Did = settings.Did,
			PdsUrl = settings.PdsUrl,
			SystemPrompt = settings.SystemPrompt
		};

		// Проверяем актуальность токена (обновит через RefreshToken, если истек)
		await _service.GetValidTokenAsync(botModel);

		// Определяем типы медиа
		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
		var videoItem = images?.FirstOrDefault(isVideo);
		var photoItems = images?.Where(s => !isVideo(s)).ToList() ?? new List<string>();

		bool success = false;

		// 1. ПУБЛИКАЦИЯ ВИДЕО
		if (videoItem != null)
		{
			await _console.Log("Обнаружено видео. Загрузка ролика в BlueSky...", settings.UserId, state.BotId);
			success = await _service.PublishPostWithVideoAsync(caption, videoItem, "video/mp4", botModel);
		}
		// 2. ПУБЛИКАЦИЯ ФОТО (до 4 шт)
		else if (photoItems.Any())
		{
			await _console.Log($"Публикация {photoItems.Count} фото в BlueSky...", settings.UserId, state.BotId);
			success = await _service.PublishPostWithImagesAsync(caption, photoItems, botModel);
		}
		// 3. ТЕКСТОВЫЙ ПОСТ
		else
		{
			await _console.Log("Публикация текстового поста в BlueSky...", settings.UserId, state.BotId);
			success = await _service.CreatePostAsync(caption, botModel);
		}

		if (!success)
		{
			throw new Exception($"Ошибка при публикации поста в BlueSky @{settings.Handle}");
		}

		await _console.Log($"Пост успешно опубликован в BlueSky @{settings.Handle}.", settings.UserId, state.BotId);
	}
}