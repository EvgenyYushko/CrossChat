using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookDailyStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DailyStoryTime",
                table: "FacebookSettings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDailyStoriesEnabled",
                table: "FacebookSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsStoryOverlayTextEnabled",
                table: "FacebookSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDailyStoryDate",
                table: "FacebookSettings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryOverlayText",
                table: "FacebookSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsedMediaIdsJson",
                table: "FacebookSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DailyStoryTime",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "IsDailyStoriesEnabled",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "IsStoryOverlayTextEnabled",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "LastDailyStoryDate",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "StoryOverlayText",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "UsedMediaIdsJson",
                table: "FacebookSettings");
        }
    }
}
