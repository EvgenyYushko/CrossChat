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

		await _service.GetValidTokenAsync(botModel);

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
			throw new Exception($"Ошибка при публикации поста в BlueSky @{settings.Handle}");
		}

		await _console.Log($"Пост успешно опубликован в BlueSky @{settings.Handle}.", settings.UserId, state.BotId);

		// === 4. ПУБЛИКАЦИЯ ПЕРВОГО КОММЕНТАРИЯ (ВЕТКА В BLUESKY) ===
		if (!string.IsNullOrWhiteSpace(state.FirstComment) && !string.IsNullOrEmpty(postUri) && !string.IsNullOrEmpty(postCid))
		{
			try
			{
				await _console.Log("Публикация первого комментария (Reply) в BlueSky...", settings.UserId, state.BotId);

				// Пауза 1.5 сек для фиксации корневого поста в репозитории PDS
				await Task.Delay(1500);

				bool replySuccess = await _service.CreateReplyAsync(state.FirstComment.Trim(), postUri, postCid, botModel);
				if (replySuccess)
				{
					await _console.Log("Первый комментарий в BlueSky успешно опубликован!", settings.UserId, state.BotId);
				}
				else
				{
					await _console.Log("⚠️ Не удалось опубликовать первый комментарий в BlueSky (основной пост опубликован).", settings.UserId, state.BotId);
				}
			}
			catch (Exception ex)
			{
				await _console.Log($"⚠️ Ошибка при создании первого комментария в BlueSky: {ex.Message}", settings.UserId, state.BotId);
			}
		}
	}
}