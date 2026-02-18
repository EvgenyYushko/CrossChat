using CrossChat.Worker.Consumers;
using MassTransit;

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
	}
}
