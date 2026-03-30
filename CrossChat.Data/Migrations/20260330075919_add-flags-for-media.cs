using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class addflagsformedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProcessAudios",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ProcessPhotos",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ProcessVideos",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessAudios",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "ProcessPhotos",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "ProcessVideos",
                table: "InstagramSettings");
        }
    }
}
