using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableTurnRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TurnRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    UserPreview = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AssistantPreview = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Move = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ResolvedReference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ResolutionConfidence = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    BoundQuestion = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    RetrievalQuery = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Intent = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    IntentConfidence = table.Column<double>(type: "REAL", nullable: false),
                    IntentRunnerUp = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Retrieved = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FocalTerms = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FocalCovered = table.Column<bool>(type: "INTEGER", nullable: true),
                    Decisions = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PacketTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelUsed = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TurnRecords_UserId_Timestamp",
                table: "TurnRecords",
                columns: new[] { "UserId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TurnRecords");
        }
    }
}
