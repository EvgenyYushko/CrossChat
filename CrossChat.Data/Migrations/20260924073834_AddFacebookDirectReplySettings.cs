using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFacebookDirectReplySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DirectReplyMode",
                table: "FacebookSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DirectTemplates",
                table: "FacebookSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDirectEnabled",
                table: "FacebookSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectReplyMode",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "DirectTemplates",
                table: "FacebookSettings");

            migrationBuilder.DropColumn(
                name: "IsDirectEnabled",
                table: "FacebookSettings");
        }
    }
}
