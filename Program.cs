using CrossChat.BackgroundServices;
using CrossChat.Data;
using CrossChat.Models;
using MassTransit;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
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

var connectionString = Environment.GetEnvironmentVariable("DB_URL_POSTGRESQL");
if (string.IsNullOrEmpty(connectionString))
{
	connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
}

builder.Services.AddDbContext<AppDbContext>(options =>
options.UseNpgsql(connectionString, npgsqlOptions =>
{
	// ВАЖНО: Указываем, что миграции лежат в проекте с данными, а не в Web
	npgsqlOptions.MigrationsAssembly("CrossChat.Data");

	// ВАЖНО: Устойчивость к сбоям сети (Retry Policy)
	npgsqlOptions.EnableRetryOnFailure(
		maxRetryCount: 5,
		maxRetryDelay: TimeSpan.FromSeconds(10),
		errorCodesToAdd: null);
}));

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

// 2. === АВТОРИЗАЦИЯ ===
builder.Services.AddAuthentication(options =>
{
	options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
	options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
	options.LoginPath = "/auth/login"; // Если не авторизован -> сюда
})
.AddGoogle(options =>
{
	// Берем ID и Secret из твоих секретов (appsettings/user-secrets)
	options.ClientId = GetConfigOrThrow("Google:ClientId");
	options.ClientSecret = GetConfigOrThrow("Google:ClientSecret");

	// Сохраняем токены (если потом захочешь обращаться к API Google)
	options.SaveTokens = true;
});

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
app.UseAuthentication(); // Кто ты?
app.UseAuthorization();  // Можно ли тебе сюда?
app.MapRazorPages();
app.MapControllers();

app.Run();

using (var scope = app.Services.CreateScope())
{
	var services = scope.ServiceProvider;
	try
	{
		var dbContext = services.GetRequiredService<AppDbContext>();

		// Эта команда применит все ожидающие миграции.
		// Если база пустая - она создаст таблицы.
		// Если база актуальна - она ничего не сделает.
		dbContext.Database.Migrate();

		// (Опционально) Log: Миграции успешно применены
	}
	catch (Exception ex)
	{
		// Если миграции упали - приложение не должно запускаться, 
		// иначе оно будет работать с неверной схемой данных.
		var logger = services.GetRequiredService<ILogger<Program>>();
		logger.LogError(ex, "An error occurred while migrating the database.");
		throw; // Пробрасываем ошибку, чтобы Render показал, что деплой упал
	}
}

string GetConfigOrThrow(string key)
{
	var value = builder.Configuration[key] ?? Environment.GetEnvironmentVariable(key);

	if (string.IsNullOrWhiteSpace(value))
	{
		throw new InvalidOperationException($"❌ ОШИБКА КОНФИГУРАЦИИ: Не найдена обязательная переменная '{key}'. Проверьте appsettings.json, User Secrets или порядок загрузки конфигов.");
	}
	return value;
}