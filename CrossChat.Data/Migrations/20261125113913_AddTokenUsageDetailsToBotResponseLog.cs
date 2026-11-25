using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageDetailsToBotResponseLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "BotResponseLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "BotResponseLogs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "BotResponseLogs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "UsageDetailsJson",
                table: "BotResponseLogs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "BotResponseLogs");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "BotResponseLogs");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "BotResponseLogs");

            migrationBuilder.DropColumn(
                name: "UsageDetailsJson",
                table: "BotResponseLogs");
        }
    }
}
