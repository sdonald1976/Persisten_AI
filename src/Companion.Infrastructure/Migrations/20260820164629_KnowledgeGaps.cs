using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GapId",
                table: "Curiosities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KnowledgeGaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SubjectConceptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRef = table.Column<Guid>(type: "TEXT", nullable: true),
                    Occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Pursuit = table.Column<int>(type: "INTEGER", nullable: false),
                    CuriosityId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeGaps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeGaps_UserId_Kind_Subject",
                table: "KnowledgeGaps",
                columns: new[] { "UserId", "Kind", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeGaps_UserId_Status",
                table: "KnowledgeGaps",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeGaps");

            migrationBuilder.DropColumn(
                name: "GapId",
                table: "Curiosities");
        }
    }
}
