using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFirstCommentSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstComment",
                table: "NetworkStates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstComment",
                table: "NetworkStates");
        }
    }
}
