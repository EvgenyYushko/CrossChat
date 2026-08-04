using CrossChat.Data;
using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Integrations.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossChat.Worker.Facades
{
	public class SocialPublicationFacade
	{
		private readonly IPostService _postService;
		private readonly IInstagramService _instagramService;
		private readonly IFaceBookService _faceBookService;
		private readonly IBlueSkyService _blueSkyService;
		private readonly ITelegramService _telegramService;
		private readonly IXService _xService;
		private readonly IThreadsService _threadsService;
		private readonly ILogger<SocialPublicationFacade> _logger;
		private AppDbContext _appDbContext;
		public SocialPublicationFacade(IPostService postService
			, IInstagramService instagramService
			, IFaceBookService faceBookService
			, IBlueSkyService blueSkyService
			, ITelegramService telegramService
			, IXService xService
			, IThreadsService threadsService
			, ILogger<SocialPublicationFacade> logger
			, AppDbContext appDbContext)
		{
			_appDbContext = appDbContext;
			_postService = postService;
			_instagramService = instagramService;
			_faceBookService = faceBookService;
			_blueSkyService = blueSkyService;
			_telegramService = telegramService;
			_xService = xService;
			_threadsService = threadsService;
			_logger = logger;
		}

		public async Task PublishToSocialNetworkAsync(NetworkStateEntity state)
		{
			string caption = state.Caption;
			List<string> files = state.Post.Images.Select(p => p.Base64Data).ToList(); // Base64 строки из БД

			var network = (NetworkType)state.NetworkType;

			switch (network)
			{
				case NetworkType.Instagram:

					// 1. Достаем токен и настройки конкретного Instagram-бота по state.BotId
					var instaSettings = await _appDbContext.InstagramSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					// Проверки безопасности
					if (instaSettings == null || string.IsNullOrEmpty(instaSettings.AccessToken))
					{
						throw new Exception($"Не найдены настройки или AccessToken для Instagram аккаунта (BotId: {state.BotId})");
					}

					if (!instaSettings.IsActive)
					{
						throw new Exception($"Instagram бот (BotId: {state.BotId}) отключен в настройках.");
					}

					// 2. Публикуем пост в ленту
					var instaResult = await InstagramPost(caption, files, instaSettings.AccessToken);
					if (!instaResult)
					{
						throw new Exception($"Ошибка API при публикации поста в Instagram (BotId: {state.BotId})");
					}

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
					break;

				case NetworkType.Facebook:
					// Достаем настройки Facebook по state.BotId
					var fbSettings = await _appDbContext.FacebookSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (fbSettings == null || string.IsNullOrEmpty(fbSettings.PageAccessToken))
					{
						throw new Exception($"Не найдены настройки или PageAccessToken для Facebook страницы (BotId: {state.BotId})");
					}

					await FaceBookPostImages(caption, files, fbSettings.PageAccessToken, fbSettings.PageId);
					//await FaceBookStory(files, fbSettings.PageAccessToken, fbSettings.PageId);
					break;

				case NetworkType.BlueSky:
					var bskySettings = await _appDbContext.BlueSkySettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (bskySettings == null || string.IsNullOrEmpty(bskySettings.AccessToken))
					{
						throw new Exception($"Не найдены настройки для BlueSky аккаунта (BotId: {state.BotId})");
					}

					// TODO: Вызов вашего _blueSkyService с передачей bskySettings.AccessToken
					break;

				case NetworkType.TelegramPublic:
				case NetworkType.TelegramPrivate:
					// Достаем юзербота Telegram по state.BotId
					var tgUserBot = await _appDbContext.TelegramUsersBotSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (tgUserBot == null || !tgUserBot.IsActive)
					{
						throw new Exception($"Telegram UserBot (BotId: {state.BotId}) не найден или отключен.");
					}

					// TODO: Вызов вашего _telegramService
					break;

				case NetworkType.X:
					var xSettings = await _appDbContext.XSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (xSettings == null || string.IsNullOrEmpty(xSettings.AccessToken))
					{
						throw new Exception($"Не найдены настройки для X (Twitter) (BotId: {state.BotId})");
					}

					await XPost(caption, xSettings.AccessToken, files);
					break;

				case NetworkType.Threads:
					var threadsSettings = await _appDbContext.ThreadsSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (threadsSettings == null || string.IsNullOrEmpty(threadsSettings.AccessToken))
					{
						throw new Exception($"Не найдены настройки для Threads (BotId: {state.BotId})");
					}

					var threadsSuccess = await ThreadsPost(caption, files, threadsSettings.AccessToken);
					if (!threadsSuccess)
					{
						throw new Exception($"Ошибка при публикации поста в Threads )");
					}
					break;

				default:
					throw new NotImplementedException($"Публикация в {network} не реализована.");
			}
		}

		// Вспомогательные методы отправки (принимают токен параметром)
		public async Task<bool> InstagramPost(string caption, List<string> files, string accessToken)
		{
			var instaResult = await _instagramService.CreateMediaAsync(files, accessToken, caption);
			return instaResult.Success;
		}

		public async Task<string?> InstagramStory(List<string> files, string accessToken)
		{
			return await _instagramService.PublishStoryFromBase64(files.FirstOrDefault(), accessToken);
		}

		public async Task<bool> FaceBookPostImages(string caption, List<string> files, string pageAcessToken, string pageId)
		{
			return await _faceBookService.PublishToPageAsync(caption, pageAcessToken, pageId, files);
		}

		public async Task<bool> FaceBookStory(List<string> files, string pageAcessToken, string pageId)
		{
			return await _faceBookService.PublishStoryAsync(files.FirstOrDefault(), pageAcessToken, pageId);
		}

		public async Task<bool> FaceBookPostReels(string caption, string base64Video, string pageAcessToken, string pageId)
		{
			return await _faceBookService.PublishReelAsync(caption, base64Video, pageAcessToken, pageId);
		}

		public Task<bool> XPost(string caption, string accessToken, List<string> files = null, string videoBase64 = null)
		{
			if (files is not null && files.Count > 0)
			{
				return _xService.CreateImagePost(caption, files, accessToken);
			}
			else if (!string.IsNullOrEmpty(videoBase64))
			{
				return _xService.CreateVideoPost(caption, videoBase64, accessToken);
			}
			else
			{
				return _xService.CreateTextPostAsync(caption, accessToken);
			}
		}

		public Task<bool> ThreadsPost(string caption, List<string> base64Image, string accessToken)
		{
			return _threadsService.CreatePostAsync(caption, base64Image, accessToken);
		}
	}
}
