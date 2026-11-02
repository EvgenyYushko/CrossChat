using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlueSkyHashtagEngagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EngagementCommentTemplates",
                table: "BlueSkySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EngagementHashtags",
                table: "BlueSkySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHashtagEngagementEnabled",
                table: "BlueSkySettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHashtagEngagementAt",
                table: "BlueSkySettings",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsedPostUrisJson",
                table: "BlueSkySettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EngagementCommentTemplates",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "EngagementHashtags",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "IsHashtagEngagementEnabled",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "LastHashtagEngagementAt",
                table: "BlueSkySettings");

            migrationBuilder.DropColumn(
                name: "UsedPostUrisJson",
                table: "BlueSkySettings");
        }
    }
}
