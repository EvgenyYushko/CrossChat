using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramMediaGallery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAutoMediaEnabled",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InstagramMediaCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstagramSettingsId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramMediaCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramMediaCategories_InstagramSettings_InstagramSetting~",
                        column: x => x.InstagramSettingsId,
                        principalTable: "InstagramSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstagramMediaItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstagramSettingsId = table.Column<int>(type: "integer", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: false),
                    MediaType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GoogleDriveFileId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ThumbnailBase64 = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramMediaItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramMediaItems_InstagramMediaCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "InstagramMediaCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstagramCustomerSentMedias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    MediaItemId = table.Column<int>(type: "integer", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstagramCustomerSentMedias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstagramCustomerSentMedias_InstagramBotCustomers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "InstagramBotCustomers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InstagramCustomerSentMedias_InstagramMediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "InstagramMediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstagramCustomerSentMedias_CustomerId_MediaItemId",
                table: "InstagramCustomerSentMedias",
                columns: new[] { "CustomerId", "MediaItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstagramCustomerSentMedias_MediaItemId",
                table: "InstagramCustomerSentMedias",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramMediaCategories_InstagramSettingsId",
                table: "InstagramMediaCategories",
                column: "InstagramSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramMediaItems_CategoryId",
                table: "InstagramMediaItems",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_InstagramMediaItems_InstagramSettingsId_CategoryId",
                table: "InstagramMediaItems",
                columns: new[] { "InstagramSettingsId", "CategoryId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstagramCustomerSentMedias");

            migrationBuilder.DropTable(
                name: "InstagramMediaItems");

            migrationBuilder.DropTable(
                name: "InstagramMediaCategories");

            migrationBuilder.DropColumn(
                name: "IsAutoMediaEnabled",
                table: "InstagramSettings");
        }
    }
}
