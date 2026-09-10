using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CrossChat.Data
{
	// РАскоментить и разобратьсяя, суть в том что бы при миграциях необязетльно включён был Redis
	//public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
	//{
	//	public AppDbContext CreateDbContext(string[] args)
	//	{
	//		// Ищем appsettings.json в основном проекте запуска CrossChat
	//		var basePath = Directory.GetCurrentDirectory();

	//		// Если консоль запущена из папки данных, делаем шаг назад в проект CrossChat
	//		if (!File.Exists(Path.Combine(basePath, "appsettings.json")))
	//		{
	//			var fallbackPath = Path.Combine(basePath, "..", "CrossChat");
	//			if (Directory.Exists(fallbackPath))
	//			{
	//				basePath = Path.GetFullPath(fallbackPath);
	//			}
	//		}

	//		var configuration = new ConfigurationBuilder()
	//			.SetBasePath(basePath)
	//			.AddJsonFile("appsettings.json", optional: true)
	//			.AddJsonFile("appsettings.Development.json", optional: true)
	//			.AddEnvironmentVariables()
	//			.Build();

	//		// Берем твою строку подключения к PostgreSQL
	//		var connectionString = configuration.GetConnectionString("DefaultConnection");

	//		var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
	//		optionsBuilder.UseNpgsql(connectionString);

	//		return new AppDbContext(optionsBuilder.Options);
	//	}
	//}
}