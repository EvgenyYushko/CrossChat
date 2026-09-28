using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateYouTubeTableActual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YouTubeSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChannelTitle = table.Column<string>(type: "text", nullable: false),
                    CustomUrl = table.Column<string>(type: "text", nullable: true),
                    ProfilePictureUrl = table.Column<string>(type: "text", nullable: true),
                    AccessToken = table.Column<string>(type: "text", nullable: true),
                    RefreshToken = table.Column<string>(type: "text", nullable: true),
                    TokenExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SubscriberCount = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    VideoCount = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YouTubeSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YouTubeSettings_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_YouTubeSettings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeSettings_ProfileId",
                table: "YouTubeSettings",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeSettings_UserId",
                table: "YouTubeSettings",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YouTubeSettings");
        }
    }
}
