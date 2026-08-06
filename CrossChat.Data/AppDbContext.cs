using CrossChat.Data.Entities;
using CrossChat.Data.Entities.Posting;
using Microsoft.EntityFrameworkCore;

namespace CrossChat.Data
{
	public class AppDbContext : DbContext
	{
		public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

		public DbSet<User> Users { get; set; }
		public DbSet<InstagramSettings> InstagramSettings { get; set; }
		public DbSet<FacebookSettings> FacebookSettings { get; set; }
		public DbSet<TelegramSettings> TelegramSettings { get; set; }
		public DbSet<TelegramUserBotSettings> TelegramUsersBotSettings { get; set; }
		public DbSet<TelegramChannelSettings> TelegramChannelSettings { get; set; }
		public DbSet<ThreadsSettings> ThreadsSettings { get; set; }
		public DbSet<BlueSkySettings> BlueSkySettings { get; set; }
		public DbSet<XSettings> XSettings { get; set; }
		public DbSet<InstagramBotCustomer> InstagramBotCustomers { get; set; }
		public DbSet<BotResponseLog> BotResponseLogs { get; set; }
		public DbSet<Profile> Profile { get; set; }


		public DbSet<PostEntity> Posts { get; set; }
		public DbSet<PostImageEntity> PostImages { get; set; }
		public DbSet<NetworkStateEntity> NetworkStates { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			// Ускоряем поиск при входе
			modelBuilder.Entity<User>().HasIndex(u => u.GoogleId).IsUnique();
			modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
		}
	}
}