using CrossChat.BackgroundServices;
using CrossChat.Models;
using MassTransit;
using Quartz;
using StackExchange.Redis;
using static CrossChat.Worker.WorkerInstaller;

var builder = WebApplication.CreateBuilder(args);

// --- ИСПРАВЛЕНИЕ: ПОДКЛЮЧАЕМ КОНФИГ В САМОМ НАЧАЛЕ ---
// Сначала загружаем секретный файл, чтобы настройки стали доступны
builder.Configuration.AddJsonFile("/etc/secrets/SocialMedia", optional: true, reloadOnChange: true);

// Регистрируем настройки в DI (чтобы использовать через IOptions<T>)
builder.Services.Configure<SocialMediaSettings>(builder.Configuration.GetSection("SocialMedia"));
builder.Services.Configure<ExternalHostingsSettings>(builder.Configuration.GetSection("ExternalHostingsSettings"));

builder.Services.AddControllers();

// 1. == НАСТРОЙКА REDIS ==
// Теперь этот метод найдет значение, так как файл уже загружен выше
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
    x.AddWorkerConsumers();
    x.AddQuartzConsumers();
    x.AddPublishMessageScheduler();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqUrl);
        cfg.UsePublishMessageScheduler();
        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddLogging();
builder.Services.AddRazorPages();
builder.Services.AddHostedService<HealthCheckBackgroundService>();
builder.Services.AddHttpClient();
builder.Services.AddOpenApi();

var app = builder.Build();

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
    var value = builder.Configuration[key] ?? Environment.GetEnvironmentVariable(key);

    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"❌ ОШИБКА КОНФИГУРАЦИИ: Не найдена обязательная переменная '{key}'. Проверьте appsettings.json, User Secrets или порядок загрузки конфигов.");
    }
    return value;
}