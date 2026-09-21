using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramStoryOverlay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsStoryOverlayTextEnabled",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StoryOverlayText",
                table: "InstagramSettings",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsStoryOverlayTextEnabled",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "StoryOverlayText",
                table: "InstagramSettings");
        }
    }
}
