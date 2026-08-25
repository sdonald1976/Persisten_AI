using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFrameSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FrameBoundaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SceneRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    StatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceKind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EvidenceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EvidenceStatement = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DeactivatedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrameBoundaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FrameSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SceneRef = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CharactersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveCompanionCharacterId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Narration = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Continuity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    NarratorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    NarratorCharacterId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ViewpointCharacterId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Person = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    EnteredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTransitionAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedKeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    TransitionLogJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FrameSessions", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FrameBoundaries_UserId_ConversationId_SceneRef_Status",
                table: "FrameBoundaries",
                columns: new[] { "UserId", "ConversationId", "SceneRef", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FrameSessions_EndedAt",
                table: "FrameSessions",
                column: "EndedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FrameSessions_UserId_ConversationId_Status",
                table: "FrameSessions",
                columns: new[] { "UserId", "ConversationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FrameBoundaries");

            migrationBuilder.DropTable(
                name: "FrameSessions");
        }
    }
}
