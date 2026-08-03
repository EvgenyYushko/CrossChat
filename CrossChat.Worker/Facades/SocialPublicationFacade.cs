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
		private readonly ILogger<SocialPublicationFacade> _logger;
		private AppDbContext _appDbContext;
		public SocialPublicationFacade(IPostService postService
			, IInstagramService instagramService
			, IFaceBookService faceBookService
			, IBlueSkyService blueSkyService
			, ITelegramService telegramService
			, IXService xService
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
					if (files != null && files.Any())
					{
						try
						{
							string? storyId = await InstagramStory(files, instaSettings.AccessToken);
							if (storyId is not null)
							{
								_logger.LogInformation("✅ Instagram Story успешно опубликована (StoryId: {StoryId})", storyId);
							}
						}
						catch (Exception ex)
						{
							_logger.LogError(ex, "Ошибка при отправке Instagram Story для бота {BotId}", state.BotId);
						}
					}
					break;

				case NetworkType.Facebook:
					// Достаем настройки Facebook по state.BotId
					var fbSettings = await _appDbContext.FacebookSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (fbSettings == null || string.IsNullOrEmpty(fbSettings.PageAccessToken))
					{
						throw new Exception($"Не найдены настройки или PageAccessToken для Facebook страницы (BotId: {state.BotId})");
					}

					// TODO: Вызов вашего _facebookService с передачей fbSettings.PageAccessToken
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

					// TODO: Вызов вашего _xService
					break;

				case NetworkType.Threads:
					var threadsSettings = await _appDbContext.ThreadsSettings
						.FirstOrDefaultAsync(x => x.Id == state.BotId);

					if (threadsSettings == null || string.IsNullOrEmpty(threadsSettings.AccessToken))
					{
						throw new Exception($"Не найдены настройки для Threads (BotId: {state.BotId})");
					}

					// TODO: Вызов вашего _threadsService
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
	}
}
