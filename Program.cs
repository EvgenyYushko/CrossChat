using CrossChat.BackgroundServices;
using CrossChat.Models;
using MassTransit;
using Quartz;
using StackExchange.Redis;
using static CrossChat.Worker.WorkerInstaller;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// 1. == НАСТРОЙКА REDIS ==

var redisConn = GetConfigOrThrow("ExternalHostingsSettings:Redis");
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConn));

var rabbitMqUrl = GetConfigOrThrow("ExternalHostingsSettings:RabbitMq");

// 1. == ДОБАВЛЯЕМ QUARTZ ==
builder.Services.AddQuartz(q =>
{
	q.UseMicrosoftDependencyInjectionJobFactory();
});
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// === НАСТРОЙКА MASSTRANSIT (RABBITMQ) ===
builder.Services.AddMassTransit(x =>
{
	// Твои консьюмеры (WebhookConsumer, ReplyConsumer)
	x.AddWorkerConsumers();

	// !!! ВАЖНО: Добавляем системные консьюмеры Quartz !!!
	// Именно они слушают команду "SchedulePublish" и ставят задачу в таймер
	x.AddQuartzConsumers();

	// Хелпер для планирования
	x.AddPublishMessageScheduler();

	x.UsingRabbitMq((context, cfg) =>
	{
		cfg.Host(rabbitMqUrl);

		// Говорим MassTransit использовать планировщик
		cfg.UsePublishMessageScheduler();

		// Это создаст очереди:
		// - WebhookConsumer
		// - ReplyConsumer
		// - QuartzConsumer (вот его у тебя не хватало!)
		cfg.ConfigureEndpoints(context);
	});
});
// =========================================

builder.Services.AddLogging();

builder.Services.AddRazorPages();
builder.Configuration.AddJsonFile("/etc/secrets/SocialMedia", optional: true, reloadOnChange: true);
builder.Services.Configure<SocialMediaSettings>(builder.Configuration.GetSection("SocialMedia"));
builder.Services.Configure<ExternalHostingsSettings>(builder.Configuration.GetSection("ExternalHostingsSettings"));
builder.Services.AddHostedService<HealthCheckBackgroundService>();

builder.Services.AddHttpClient();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthorization();
app.MapRazorPages();
app.MapControllers();

app.Run();

string GetConfigOrThrow(string key)
{
	var value = builder.Configuration[key];

	if (string.IsNullOrWhiteSpace(value))
	{
		// Громко падаем, если нет критически важной настройки
		throw new InvalidOperationException($"❌ ОШИБКА КОНФИГУРАЦИИ: Не найдена обязательная переменная '{key}'. Проверьте appsettings.json или переменные окружения на хостинге.");
	}
	return value;
}
