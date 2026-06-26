using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class TestMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelegramSettings_ProfileId",
                table: "TelegramSettings");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramSettings_ProfileId",
                table: "TelegramSettings",
                column: "ProfileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TelegramSettings_ProfileId",
                table: "TelegramSettings");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramSettings_ProfileId",
                table: "TelegramSettings",
                column: "ProfileId");
        }
    }
}
