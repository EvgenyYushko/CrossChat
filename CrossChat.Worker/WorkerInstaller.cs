using CrossChat.Integrations.Interfaces;
using CrossChat.Integrations.Services;
using CrossChat.Worker.Consumers;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Protos.GoogleGeminiService;

namespace CrossChat.Worker
{
	public static class WorkerInstaller
	{
		// Этот метод мы вызовем в Program.cs основного сайта
		public static void AddWorkerConsumers(this IBusRegistrationConfigurator x)
		{
			// MassTransit просканирует сборку, где лежит WebhookConsumer, 
			// и зарегистрирует все консьюмеры, которые найдет.
			x.AddConsumersFromNamespaceContaining<WebhookConsumer>();
		}

		public static void AddWorkerServices(this IServiceCollection services, string token)
		{
			// Регистрируем HttpClient для Инстаграма
			services.AddHttpClient<IInstagramService, InstagramService>(client =>
			{
				client.BaseAddress = new Uri("https://graph.instagram.com/");
			});

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
		}
	}
}
