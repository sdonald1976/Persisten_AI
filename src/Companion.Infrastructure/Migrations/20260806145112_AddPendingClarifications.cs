using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingClarifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingClarifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", nullable: false),
                    AmbiguityType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ResolvedProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingClarifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingClarifications_ConversationId_Status",
                table: "PendingClarifications",
                columns: new[] { "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingClarifications_UserId_Status",
                table: "PendingClarifications",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingClarifications");
        }
    }
}
