using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegramJoinRequestSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveJoinRequests",
                table: "TelegramChannelSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnJoinRequests",
                table: "TelegramChannelSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyOnMemberLeft",
                table: "TelegramChannelSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApproveJoinRequests",
                table: "TelegramChannelSettings");

            migrationBuilder.DropColumn(
                name: "NotifyOnJoinRequests",
                table: "TelegramChannelSettings");

            migrationBuilder.DropColumn(
                name: "NotifyOnMemberLeft",
                table: "TelegramChannelSettings");
        }
    }
}
