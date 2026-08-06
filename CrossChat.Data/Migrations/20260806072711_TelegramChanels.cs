using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramChanels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TelegramUserId",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TelegramChannelSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelTitle = table.Column<string>(type: "text", nullable: false),
                    ChannelUsername = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    ProfileId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramChannelSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramChannelSettings_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TelegramChannelSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChannelSettings_ProfileId",
                table: "TelegramChannelSettings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramChannelSettings_UserId",
                table: "TelegramChannelSettings",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramChannelSettings");

            migrationBuilder.DropColumn(
                name: "TelegramUserId",
                table: "Users");
        }
    }
}
