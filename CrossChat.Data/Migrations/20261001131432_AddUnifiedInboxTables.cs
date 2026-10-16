using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrossChat.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedInboxTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxConversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProfileId = table.Column<int>(type: "integer", nullable: false),
                    NetworkType = table.Column<int>(type: "integer", nullable: false),
                    BotId = table.Column<int>(type: "integer", nullable: false),
                    ExternalChatId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ExternalUserId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    LastMessageSnippet = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false),
                    IsHumanTakeover = table.Column<bool>(type: "boolean", nullable: false),
                    HumanTakeoverUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxConversations_Profile_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "Profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<int>(type: "integer", nullable: false),
                    ExternalMessageId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    IsIncoming = table.Column<bool>(type: "boolean", nullable: false),
                    IsSentByBot = table.Column<bool>(type: "boolean", nullable: false),
                    MediaUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MediaType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsReadByManager = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxMessages_InboxConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "InboxConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxConversations_NetworkType_BotId_ExternalUserId",
                table: "InboxConversations",
                columns: new[] { "NetworkType", "BotId", "ExternalUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxConversations_ProfileId",
                table: "InboxConversations",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ConversationId",
                table: "InboxMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ExternalMessageId",
                table: "InboxMessages",
                column: "ExternalMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "InboxConversations");
        }
    }
}
