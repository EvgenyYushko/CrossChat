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

		await _service.PublishPostWithImagesAsync(caption, images, botModel);

		//if (videoModel is not null)
		//{
		//	var videoBlob = await _blueSkyService.UploadVideoFromBase64Async(videoModel.Base64Video, videoModel.MimeType);
		//	var ratio = new AspectRatio { Width = 9, Height = 16 };
		//	success = await _blueSkyService.CreatePostWithVideoAsync(description, videoBlob, ratio);
		//}
		//else 

		await _console.Log($"Пост успешно опубликован в BlueSky @{settings.Handle}.", settings.UserId, state.BotId);
	}
}