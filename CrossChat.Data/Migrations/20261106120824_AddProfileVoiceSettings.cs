using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileVoiceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProfileVoiceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VoiceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Style = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Pace = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Accent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Scene = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SampleContext = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ElevenLabsVoiceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ElevenLabsApiKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProfileVoiceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProfileVoiceSettings_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProfileVoiceSettings_ProfileId",
                table: "ProfileVoiceSettings",
                column: "ProfileId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProfileVoiceSettings");
        }
    }
}
