using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlueSkyCommentsAutoReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommentPrompt",
                table: "BlueSkySettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CommentReplyMode",
                table: "BlueSkySettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CommentTemplates",
                table: "BlueSkySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCommentsEnabled",
                table: "BlueSkySettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDirectEnabled",
                table: "BlueSkySettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommentPrompt",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "CommentReplyMode",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "CommentTemplates",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "IsCommentsEnabled",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "IsDirectEnabled",
                table: "BlueSkySettings");
        }
    }
}
