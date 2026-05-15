using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class TelegramUserBot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramUsersBotSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DcId = table.Column<int>(type: "integer", nullable: false),
                    AuthKey = table.Column<string>(type: "text", nullable: false),
                    TgUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProxyHost = table.Column<string>(type: "text", nullable: true),
                    ProxyPort = table.Column<int>(type: "integer", nullable: true),
                    ProxyUser = table.Column<string>(type: "text", nullable: true),
                    ProxyPass = table.Column<string>(type: "text", nullable: true),
                    SessionData = table.Column<byte[]>(type: "bytea", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUsersBotSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramUsersBotSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUsersBotSettings_UserId",
                table: "TelegramUsersBotSettings",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramUsersBotSettings");
        }
    }
}
