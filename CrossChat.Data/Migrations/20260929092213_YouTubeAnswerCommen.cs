using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class YouTubeAnswerCommen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommentPrompt",
                table: "YouTubeSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "CommentReplyMode",
                table: "YouTubeSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CommentTemplates",
                table: "YouTubeSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCommentsEnabled",
                table: "YouTubeSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCommentProcessedAt",
                table: "YouTubeSettings",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommentPrompt",
                table: "YouTubeSettings");

            migrationBuilder.DropColumn(
                name: "CommentReplyMode",
                table: "YouTubeSettings");

            migrationBuilder.DropColumn(
                name: "CommentTemplates",
                table: "YouTubeSettings");

            migrationBuilder.DropColumn(
                name: "IsCommentsEnabled",
                table: "YouTubeSettings");

            migrationBuilder.DropColumn(
                name: "LastCommentProcessedAt",
                table: "YouTubeSettings");
        }
    }
}
