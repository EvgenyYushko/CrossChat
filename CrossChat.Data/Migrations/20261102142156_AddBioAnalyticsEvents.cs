using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBioAnalyticsEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BioPageEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BioPageId = table.Column<int>(type: "integer", nullable: false),
                    BioLinkItemId = table.Column<int>(type: "integer", nullable: true),
                    EventType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    TrafficSource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RefererUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeviceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OperatingSystem = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Browser = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BioPageEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BioPageEvents_BioLinkItems_BioLinkItemId",
                        column: x => x.BioLinkItemId,
                        principalTable: "BioLinkItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BioPageEvents_BioPages_BioPageId",
                        column: x => x.BioPageId,
                        principalTable: "BioPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BioPageEvents_BioLinkItemId",
                table: "BioPageEvents",
                column: "BioLinkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BioPageEvents_BioPageId_CreatedAtUtc",
                table: "BioPageEvents",
                columns: new[] { "BioPageId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BioPageEvents");
        }
    }
}
