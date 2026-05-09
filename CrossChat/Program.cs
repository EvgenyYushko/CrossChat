using CrossChat.BackgroundServices;
using CrossChat.Data;
using CrossChat.Helpers;
using CrossChat.Models;
using CrossChat.Worker.Jobs;
using CrossChat.Worker.Models;
using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Console;
using Quartz;
using StackExchange.Redis;
using static CrossChat.Worker.WorkerInstaller;

string GEMINI_API_KEY = "GEMINI_API_KEY";

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
// 1. Получаем строку подключения из конфига


// 2. Создаем подключение (Multiplexer)
// Мы делаем это прямо здесь, чтобы использовать его для DataProtection

var redisConnString = GetConfigOrThrow("ExternalHostingsSettings:Redis");
var redisMultiplexer = ConnectionMultiplexer.Connect(redisConnString);

builder.Services.AddSingleton<IConnectionMultiplexer>(redisMultiplexer);

builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(() => redisMultiplexer.GetDatabase(), "DataProtection-Keys")
    .SetApplicationName("CrossChat");

var rabbitMqUrl = GetConfigOrThrow("ExternalHostingsSettings:RabbitMq");

// 1. == ДОБАВЛЯЕМ QUARTZ ==
builder.Services.AddQuartz(q =>
{
	q.UseMicrosoftDependencyInjectionJobFactory();

	var jobKey = new JobKey("TokenRefreshJob");
    q.AddJob<TokenRefreshJob>(opts => opts.WithIdentity(jobKey));

    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("TokenRefreshJob-Trigger")
        .WithCronSchedule("0 30 * * * ?"));

	// 2
	var joBlueSkybKey = new JobKey("BluesSkyAnswerJob");
    q.AddJob<BluesSkyAnswerJob>(opts => opts.WithIdentity(joBlueSkybKey));

	 q.AddTrigger(opts => opts
        .ForJob(joBlueSkybKey)
        .WithIdentity("BluesSkyAnswerJob-Trigger")
        .WithCronSchedule("0 15,45 * * * ?"));

	// 3
	var jobFaceBookbKey = new JobKey("FaceBookAnswerJob");
    q.AddJob<FaceBookAnswerJob>(opts => opts.WithIdentity(jobFaceBookbKey));

	 q.AddTrigger(opts => opts
        .ForJob(jobFaceBookbKey)
        .WithIdentity("FaceBookAnswerJob-Trigger")
        .WithCronSchedule("0 10,20,30,40,50,59 * * * ?"));

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

	options.Events.OnRemoteFailure = context =>
    {
        // Если произошла ошибка (нажали "Назад", протух токен и т.д.)
        context.Response.Redirect("/auth/login"); // Редиректим обратно на вход
        context.HandleResponse(); // Говорим "Я обработал ошибку, не падай"
        return Task.CompletedTask;
    };
});

// === НАСТРОЙКА MASSTRANSIT (RABBITMQ) ===
builder.Services.AddMassTransit(x =>
{
	x.AddWorkerConsumers();
	var geminiToken = GetConfigOrThrow(GEMINI_API_KEY);
	x.AddWorkerServices(geminiToken);
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

builder.Logging.ClearProviders(); // Удаляем стандартные провайдеры
// Регистрируем наш форматтер и говорим консоли использовать его
builder.Logging.AddConsole(options => options.FormatterName = "clean")
    .AddConsoleFormatter<CleanConsoleFormatter, ConsoleFormatterOptions>();

builder.Services.AddDistributedMemoryCache(); // Нужно для сессий
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(10);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

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

app.UseSession();
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