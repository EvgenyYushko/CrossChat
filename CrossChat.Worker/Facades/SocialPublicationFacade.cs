using CrossChat.Data.Entities.Posting;
using CrossChat.Integrations.Enums;
using CrossChat.Worker.Publishers.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging; // Обязательно для GetKeyedService

namespace CrossChat.Worker.Facades
{
	public class SocialPublicationFacade
	{
		private readonly IServiceProvider _serviceProvider;
		private readonly ILogger<SocialPublicationFacade> _logger;

		public SocialPublicationFacade(IServiceProvider serviceProvider, ILogger<SocialPublicationFacade> logger)
		{
			_serviceProvider = serviceProvider;
			_logger = logger;
		}

		public async Task PublishToSocialNetworkAsync(NetworkStateEntity state)
		{
			var network = (NetworkType)state.NetworkType;
			var images = state.Post.Images.Select(p => p.Base64Data).ToList();

			// ДОСТАЕМ СЕРВИС НАПРЯМУЮ ПО КЛЮЧУ ENUM (Keyed Service)
			var publisher = _serviceProvider.GetKeyedService<ISocialPublisher>(network);

			if (publisher == null)
			{
				throw new NotImplementedException($"Публикация в соцсеть '{network}' не зарегистрирована в Keyed Services.");
			}

			// Вызываем публикацию
			await publisher.PublishAsync(state, state.Caption, images);
		}
	}
}