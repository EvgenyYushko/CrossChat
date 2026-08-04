using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Models.Site;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Consumers.BlueSky;
using CrossChat.Worker.Consumers.FaceBook;
using CrossChat.Worker.Consumers.Instagram;
using CrossChat.Worker.Consumers.Threads;
using CrossChat.Worker.Facades;
using CrossChat.Worker.Models;
using CrossChat.Worker.Services;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Protos.GoogleGeminiService;
using Tweetinvi;

namespace CrossChat.Worker
{
	public static class WorkerInstaller
	{
		// Этот метод мы вызовем в Program.cs основного сайта
		public static void AddWorkerConsumers(this IBusRegistrationConfigurator x)
		{
			// MassTransit просканирует сборку, где лежит WebhookConsumer, 
			// и зарегистрирует все консьюмеры, которые найдет.
			x.AddConsumer<ThreadsReplyConsumer>(typeof(ThreadsReplyDefinition)); // Явная регистрация
			x.AddConsumer<ThreadsPublishConsumer>();
			x.AddConsumer<BlueSkyReplyConsumer>(typeof(BlueSkyReplyDefinition));
			x.AddConsumer<FaceBookReplyConsumer>(typeof(FaceBookReplyDefinition));
			x.AddConsumersFromNamespaceContaining<WebhookConsumer>();
		}

		public static void AddWorkerServices(this IServiceCollection services, string token, SiteSettings siteSettings)
		{
			// Регистрируем HttpClient для Инстаграма
			services.AddSingleton(siteSettings);

			services.AddHttpClient<IInstagramService, InstagramService>(client =>
			{
				client.BaseAddress = new Uri("https://graph.instagram.com/");
			});

			services.AddHttpClient<IThreadsService, ThreadsService>(client =>
			{
				client.BaseAddress = new Uri("https://graph.threads.net/");
			});

			services.AddSingleton<IBlueSkyService, BlueSkyService>();

			services.AddSingleton<IXService>(provider =>
			{
				var logger = provider.GetService<ILogger<XService>>();
				var options = provider.GetService<IOptions<SocialMediaSettings>>();

				var consumerKey = options.Value.XConsumerKey;
				var consumerApiSecret = options.Value.XConsumerApiSecret;
				var accessToken = options.Value.XAccessToken;
				var accessTokenSecret = options.Value.XAccessTokenSecret;

				var client = new TwitterClient(consumerKey, consumerApiSecret, accessToken, accessTokenSecret);

				return new XService(logger, client);
			});

			services.AddSingleton<IFaceBookService, FaceBookService>();
			services.AddSingleton<ITelegramUserBotService, TelegramUserBotService>();

			var channel = GrpcChannel.ForAddress("https://google-services-kdg8.onrender.com", new GrpcChannelOptions
			{
				HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler())
			});
			services.AddSingleton(new GeminiService.GeminiServiceClient(channel));

			services.AddScoped<IAiService>(provider =>
			{
				var client = provider.GetService<GeminiService.GeminiServiceClient>();
				return new AiService(client, token);
			});

			services.AddSingleton<ITelegramService, TelegramService>();
			services.AddScoped<SocialPublicationFacade>();

			services.AddScoped<IPostService, PostService>();
		}
	}
}
