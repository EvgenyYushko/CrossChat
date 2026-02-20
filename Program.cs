using CrossChat.BackgroundServices;
using CrossChat.Data;
using CrossChat.Models;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
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
	options.ClaimActions.MapJsonKey("urn:google:picture", "picture", "url");
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

using (var scope = app.Services.CreateScope())
{
	var services = scope.ServiceProvider;
	var logger = services.GetRequiredService<ILogger<Program>>();
	try
	{
		var context = services.GetRequiredService<AppDbContext>();

		// Добавляем лог перед началом
		logger.LogInformation("⏳ Начинаю применение миграций...");

		// Получаем список миграций, которые нужно применить
		var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
		if (pendingMigrations.Any())
		{
			logger.LogInformation($"Найдено {pendingMigrations.Count()} новых миграций. Применяю...");
			await context.Database.MigrateAsync();
			logger.LogInformation("✅ Миграции успешно применены!");
		}
		else
		{
			logger.LogInformation("👌 База данных уже актуальна (миграций нет).");
		}
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "❌ КРИТИЧЕСКАЯ ОШИБКА МИГРАЦИИ БАЗЫ ДАННЫХ");
		// Не пробрасываем throw, чтобы приложение хотя бы запустилось и мы увидели логи
	}
}

if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
	ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication(); // Кто ты?
app.UseAuthorization();  // Можно ли тебе сюда?
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