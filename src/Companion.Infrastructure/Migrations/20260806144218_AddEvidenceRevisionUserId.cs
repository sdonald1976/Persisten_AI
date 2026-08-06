using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceRevisionUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Revisions_MemoryId",
                table: "Revisions");

            migrationBuilder.DropIndex(
                name: "IX_Evidence_MemoryId",
                table: "Evidence");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Revisions",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Evidence",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Backfill ownership from the owning memory so existing rows are scoped correctly.
            // Evidence/revisions reference either a semantic or an episodic memory (by MemoryKind),
            // so pull UserId from whichever table owns the MemoryId.
            foreach (var table in new[] { "Evidence", "Revisions" })
            {
                migrationBuilder.Sql(
                    $"UPDATE {table} SET UserId = (SELECT UserId FROM SemanticMemories WHERE SemanticMemories.Id = {table}.MemoryId) " +
                    $"WHERE MemoryKind = 'Semantic' AND EXISTS (SELECT 1 FROM SemanticMemories WHERE SemanticMemories.Id = {table}.MemoryId);");
                migrationBuilder.Sql(
                    $"UPDATE {table} SET UserId = (SELECT UserId FROM EpisodicMemories WHERE EpisodicMemories.Id = {table}.MemoryId) " +
                    $"WHERE MemoryKind = 'Episodic' AND EXISTS (SELECT 1 FROM EpisodicMemories WHERE EpisodicMemories.Id = {table}.MemoryId);");
            }

            migrationBuilder.CreateIndex(
                name: "IX_Revisions_UserId_MemoryId",
                table: "Revisions",
                columns: new[] { "UserId", "MemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_UserId_MemoryId",
                table: "Evidence",
                columns: new[] { "UserId", "MemoryId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Revisions_UserId_MemoryId",
                table: "Revisions");

            migrationBuilder.DropIndex(
                name: "IX_Evidence_UserId_MemoryId",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Revisions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Evidence");

            migrationBuilder.CreateIndex(
                name: "IX_Revisions_MemoryId",
                table: "Revisions",
                column: "MemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_MemoryId",
                table: "Evidence",
                column: "MemoryId");
        }
    }
}
