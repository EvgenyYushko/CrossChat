using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstagramDailyStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DailyStoryTime",
                table: "InstagramSettings",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDailyStoriesEnabled",
                table: "InstagramSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDailyStoryDate",
                table: "InstagramSettings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsedMediaIdsJson",
                table: "InstagramSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DailyStoryTime",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "IsDailyStoriesEnabled",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "LastDailyStoryDate",
                table: "InstagramSettings");

            migrationBuilder.DropColumn(
                name: "UsedMediaIdsJson",
                table: "InstagramSettings");
        }
    }
}
