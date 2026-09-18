using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrendRadarTables2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedHashtags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InstagramHashtagId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAutoSync = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedHashtags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedHashtags_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ViralPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TrackedHashtagId = table.Column<int>(type: "integer", nullable: false),
                    InstagramMediaId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Caption = table.Column<string>(type: "text", nullable: true),
                    MediaType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MediaUrl = table.Column<string>(type: "text", nullable: true),
                    Permalink = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LikeCount = table.Column<int>(type: "integer", nullable: false),
                    CommentsCount = table.Column<int>(type: "integer", nullable: false),
                    ExtractedHashtags = table.Column<string>(type: "text", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViralPosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ViralPosts_TrackedHashtags_TrackedHashtagId",
                        column: x => x.TrackedHashtagId,
                        principalTable: "TrackedHashtags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedHashtags_UserId",
                table: "TrackedHashtags",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ViralPosts_TrackedHashtagId",
                table: "ViralPosts",
                column: "TrackedHashtagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ViralPosts");

            migrationBuilder.DropTable(
                name: "TrackedHashtags");
        }
    }
}
