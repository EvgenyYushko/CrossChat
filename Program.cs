using CrossChat.BackgroundServices;
using CrossChat.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddLogging();

builder.Services.AddRazorPages();
builder.Configuration.AddJsonFile("/etc/secrets/SocialMedia", optional: true, reloadOnChange: true);
builder.Services.Configure<SocialMediaSettings>(builder.Configuration.GetSection("SocialMedia"));
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
