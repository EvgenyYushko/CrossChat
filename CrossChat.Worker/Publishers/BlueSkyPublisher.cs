using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.EntityFrameworkCore;

public class BlueSkyPublisher : ISocialPublisher
{
	public NetworkType Network => NetworkType.BlueSky;

	private readonly AppDbContext _db;
	private readonly IBlueSkyService _service;
	private readonly IBlueSkyTokenManager _tokenManager; // <-- ВНЕДРЯЕМ МЕНЕДЖЕР
	private readonly IBlueSkyConsole _console;

	public BlueSkyPublisher(
		AppDbContext db,
		IBlueSkyService service,
		IBlueSkyTokenManager tokenManager,
		IBlueSkyConsole console)
	{
		_db = db;
		_service = service;
		_tokenManager = tokenManager;
		_console = console;
	}

	public async Task PublishAsync(NetworkStateEntity state, string caption, List<string> images)
	{
		if (!state.BotId.HasValue)
			throw new Exception("BotId не указан для публикации в BlueSky");

		// 1. ПОЛУЧАЕМ ГАРАНТИРОВАННО СВЕЖИЙ ТОКЕН ЧЕРЕЗ МЕНЕДЖЕР (С АВТО-СОХРАНЕНИЕМ В БД!)
		var botModel = await _tokenManager.GetValidTokenAsync(state.BotId.Value);
		if (botModel == null)
			throw new Exception($"Не удалось получить валидный токен для BlueSky (BotId: {state.BotId})");

		var settings = await _db.BlueSkySettings.FirstOrDefaultAsync(x => x.Id == state.BotId);
		if (settings == null || string.IsNullOrEmpty(settings.AccessToken))
			throw new Exception($"Не найдены настройки для BlueSky (BotId: {state.BotId})");

		await _console.Log($"Начало отправки поста в BlueSky @{botModel.Handle}.", 0, state.BotId);

		bool isVideo(string s) => s.StartsWith("data:video", StringComparison.OrdinalIgnoreCase) || s.Contains("video/");
		var videoItem = images?.FirstOrDefault(isVideo);
		var photoItems = images?.Where(s => !isVideo(s)).ToList() ?? new List<string>();

		bool success = false;
		string? postUri = null;
		string? postCid = null;

		// 1. ВИДЕО
		if (videoItem != null)
		{
			await _console.Log("Обнаружено видео. Публикация в BlueSky...", settings.UserId, state.BotId);
			var result = await _service.PublishPostWithVideoAsync(caption, videoItem, "video/mp4", botModel);
			success = result.Success;
			postUri = result.Uri;
			postCid = result.Cid;
		}
		// 2. ФОТО
		else if (photoItems.Any())
		{
			await _console.Log($"Публикация {photoItems.Count} фото в BlueSky...", settings.UserId, state.BotId);
			var result = await _service.PublishPostWithImagesAsync(caption, photoItems, botModel);
			success = result.Success;
			postUri = result.Uri;
			postCid = result.Cid;
		}
		// 3. ТЕКСТ
		else
		{
			await _console.Log("Публикация текстового поста в BlueSky...", settings.UserId, state.BotId);
			var result = await _service.CreatePostAsync(caption, botModel);
			success = result.Success;
			postUri = result.Uri;
			postCid = result.Cid;
		}

		if (!success)
		{
			throw new Exception($"Ошибка при публикации поста в BlueSky @{botModel.Handle}");
		}

		await _console.Log($"Пост успешно опубликован в BlueSky @{botModel.Handle}.", 0, state.BotId);

		// === 4. ПУБЛИКАЦИЯ ПЕРВОГО КОММЕНТАРИЯ (ВЕТКА В BLUESKY) ===
		if (!string.IsNullOrWhiteSpace(state.FirstComment) && !string.IsNullOrEmpty(postUri) && !string.IsNullOrEmpty(postCid))
		{
			try
			{
				await _console.Log("Публикация первого комментария (Reply) в BlueSky...", 0, state.BotId);

				// Пауза 1.5 сек для фиксации корневого поста в репозитории PDS
				await Task.Delay(1500);

				bool replySuccess = await _service.CreateReplyAsync(state.FirstComment.Trim(), postUri, postCid, botModel);
				if (replySuccess)
				{
					await _console.Log("Первый комментарий в BlueSky успешно опубликован!", 0, state.BotId);
				}
				else
				{
					await _console.Log("⚠️ Не удалось опубликовать первый комментарий в BlueSky (основной пост опубликован).", 0, state.BotId);
				}
			}
			catch (Exception ex)
			{
				await _console.Log($"⚠️ Ошибка при создании первого комментария в BlueSky: {ex.Message}", 0, state.BotId);
			}
		}
	}
}