using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookCommentReplySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommentPrompt",
                table: "FacebookSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CommentReplyMode",
                table: "FacebookSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CommentTemplates",
                table: "FacebookSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCommentsEnabled",
                table: "FacebookSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommentPrompt",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "CommentReplyMode",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "CommentTemplates",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "IsCommentsEnabled",
                table: "FacebookSettings");
        }
    }
}
