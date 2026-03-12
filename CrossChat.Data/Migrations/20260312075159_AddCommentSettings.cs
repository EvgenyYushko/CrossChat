using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommentPrompt",
                table: "InstagramSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsCommentsEnabled",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDirectEnabled",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommentPrompt",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "IsCommentsEnabled",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "IsDirectEnabled",
                table: "InstagramSettings");
        }
    }
}
