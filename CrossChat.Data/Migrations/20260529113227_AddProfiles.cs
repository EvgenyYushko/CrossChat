using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
	/// <inheritdoc />
	public partial class AddProfiles : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "XSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "ThreadsSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "TelegramUsersBotSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "TelegramSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "InstagramSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "FacebookSettings",
				type: "integer",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "ProfileId",
				table: "BlueSkySettings",
				type: "integer",
				nullable: true);

			migrationBuilder.CreateTable(
				name: "Profile",
				columns: table => new
				{
					Id = table.Column<int>(type: "integer", nullable: false)
						.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
					UserId = table.Column<int>(type: "integer", nullable: false),
					Name = table.Column<string>(type: "text", nullable: false),
					AvatarUrl = table.Column<string>(type: "text", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Profile", x => x.Id);
					table.ForeignKey(
						name: "FK_Profile_Users_UserId",
						column: x => x.UserId,
						principalTable: "Users",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_XSettings_ProfileId",
				table: "XSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_ThreadsSettings_ProfileId",
				table: "ThreadsSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_TelegramUsersBotSettings_ProfileId",
				table: "TelegramUsersBotSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_TelegramSettings_ProfileId",
				table: "TelegramSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_InstagramSettings_ProfileId",
				table: "InstagramSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_FacebookSettings_ProfileId",
				table: "FacebookSettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_BlueSkySettings_ProfileId",
				table: "BlueSkySettings",
				column: "ProfileId");

			migrationBuilder.CreateIndex(
				name: "IX_Profile_UserId",
				table: "Profile",
				column: "UserId");

			// 2. Создаем профили для существующих пользователей
			migrationBuilder.Sql(@"
				INSERT INTO ""Profile"" (""UserId"", ""Name"")
				SELECT ""Id"", 'Основной профиль' FROM ""Users"";
			");

			// 3. Заполняем ProfileId в таблицах настроек
			migrationBuilder.Sql(@"
        UPDATE ""InstagramSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""InstagramSettings"".""UserId"");
        UPDATE ""BlueSkySettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""BlueSkySettings"".""UserId"");
        UPDATE ""FacebookSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""FacebookSettings"".""UserId"");
        UPDATE ""TelegramUsersBotSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""TelegramUsersBotSettings"".""UserId"");
        UPDATE ""TelegramSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""TelegramSettings"".""UserId"");
        UPDATE ""ThreadsSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""ThreadsSettings"".""UserId"");
        UPDATE ""XSettings"" SET ""ProfileId"" = (SELECT ""Id"" FROM ""Profile"" WHERE ""Profile"".""UserId"" = ""XSettings"".""UserId"");
    ");

			// 4. Теперь делаем колонки NOT NULL
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "InstagramSettings", nullable: false, defaultValue:0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "BlueSkySettings", nullable: false, defaultValue:0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "FacebookSettings", nullable: false, defaultValue:0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "TelegramUsersBotSettings", nullable: false, defaultValue:0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "TelegramSettings", nullable: false, defaultValue:0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "ThreadsSettings", nullable: false, defaultValue: 0);
			migrationBuilder.AlterColumn<int>(name: "ProfileId", table: "XSettings", nullable: false, defaultValue: 0);

			migrationBuilder.AddForeignKey(
				name: "FK_BlueSkySettings_Profile_ProfileId",
				table: "BlueSkySettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_FacebookSettings_Profile_ProfileId",
				table: "FacebookSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_InstagramSettings_Profile_ProfileId",
				table: "InstagramSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_TelegramUsersBotSettings_Profile_ProfileId",
				table: "TelegramUsersBotSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_TelegramSettings_Profile_ProfileId",
				table: "TelegramSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_ThreadsSettings_Profile_ProfileId",
				table: "ThreadsSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);

			migrationBuilder.AddForeignKey(
				name: "FK_XSettings_Profile_ProfileId",
				table: "XSettings",
				column: "ProfileId",
				principalTable: "Profile",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropForeignKey(
				name: "FK_BlueSkySettings_Profile_ProfileId",
				table: "BlueSkySettings");

			migrationBuilder.DropForeignKey(
				name: "FK_FacebookSettings_Profile_ProfileId",
				table: "FacebookSettings");

			migrationBuilder.DropForeignKey(
				name: "FK_InstagramSettings_Profile_ProfileId",
				table: "InstagramSettings");

			migrationBuilder.DropForeignKey(
				name: "FK_TelegramUsersBotSettings_Profile_ProfileId",
				table: "TelegramUsersBotSettings");

			migrationBuilder.DropForeignKey(
				name: "FK_TelegramSettings_Profile_ProfileId",
				table: "TelegramSettings");

			migrationBuilder.DropForeignKey(
				name: "FK_ThreadsSettings_Profile_ProfileId",
				table: "ThreadsSettings");

			migrationBuilder.DropForeignKey(
				name: "FK_XSettings_Profile_ProfileId",
				table: "XSettings");

			migrationBuilder.DropTable(
				name: "Profile");

			migrationBuilder.DropIndex(
				name: "IX_XSettings_ProfileId",
				table: "XSettings");

			migrationBuilder.DropIndex(
				name: "IX_ThreadsSettings_ProfileId",
				table: "ThreadsSettings");

			migrationBuilder.DropIndex(
				name: "IX_TelegramUsersBotSettings_ProfileId",
				table: "TelegramUsersBotSettings");

			migrationBuilder.DropIndex(
				name: "IX_TelegramSettings_ProfileId",
				table: "TelegramSettings");

			migrationBuilder.DropIndex(
				name: "IX_InstagramSettings_ProfileId",
				table: "InstagramSettings");

			migrationBuilder.DropIndex(
				name: "IX_FacebookSettings_ProfileId",
				table: "FacebookSettings");

			migrationBuilder.DropIndex(
				name: "IX_BlueSkySettings_ProfileId",
				table: "BlueSkySettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "XSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "ThreadsSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "TelegramUsersBotSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "TelegramSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "InstagramSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "FacebookSettings");

			migrationBuilder.DropColumn(
				name: "ProfileId",
				table: "BlueSkySettings");
		}
	}
}
