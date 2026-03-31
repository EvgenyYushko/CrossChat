using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class NewStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GoogleId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstagramSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    InstagramBusinessId = table.Column<string>(type: "text", nullable: true),
                    ProfilePictureUrl = table.Column<string>(type: "text", nullable: true),
                    Username = table.Column<string>(type: "text", nullable: true),
                    AccessToken = table.Column<string>(type: "text", nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    CommentPrompt = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDirectEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsCommentsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessPhotos = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessVideos = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessAudios = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramSettings",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    BotToken = table.Column<string>(type: "text", nullable: true),
                    BotUsername = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramSettings", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_TelegramSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstagramBotCustomers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstagramSettingsId = table.Column<int>(type: "integer", nullable: false),
                    InstagramSenderId = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    ProfilePictureUrl = table.Column<string>(type: "text", nullable: true),
                    IsIgnored = table.Column<bool>(type: "boolean", nullable: false),
                    CustomPrompt = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramBotCustomers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramBotCustomers_InstagramSettings_InstagramSettingsId",
                        column: x => x.InstagramSettingsId,
                        principalTable: "InstagramSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BotResponseLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    TokensSpent = table.Column<int>(type: "integer", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotResponseLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotResponseLogs_InstagramBotCustomers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "InstagramBotCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotResponseLogs_CustomerId",
                table: "BotResponseLogs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramBotCustomers_InstagramSettingsId",
                table: "InstagramBotCustomers",
                column: "InstagramSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramSettings_UserId",
                table: "InstagramSettings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleId",
                table: "Users",
                column: "GoogleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotResponseLogs");

            migrationBuilder.DropTable(
                name: "TelegramSettings");

            migrationBuilder.DropTable(
                name: "InstagramBotCustomers");

            migrationBuilder.DropTable(
                name: "InstagramSettings");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
