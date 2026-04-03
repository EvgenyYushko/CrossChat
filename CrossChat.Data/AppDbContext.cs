using CrossChat.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Data
{
	public class AppDbContext : DbContext
	{
		public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

		public DbSet<User> Users { get; set; }
		public DbSet<InstagramSettings> InstagramSettings { get; set; }
		public DbSet<TelegramSettings> TelegramSettings { get; set; }
		public DbSet<ThreadsSettings> ThreadsSettings { get; set; }
		public DbSet<InstagramBotCustomer> InstagramBotCustomers { get; set; }
		public DbSet<BotResponseLog> BotResponseLogs { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			// Ускоряем поиск при входе
			modelBuilder.Entity<User>().HasIndex(u => u.GoogleId).IsUnique();
			modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
		}
	}
}