using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <summary>
    /// R-01. Removes the two places frame state kept the user's own words.
    ///
    /// FrameBoundaries.EvidenceStatement held a verbatim statement "as evidence"; the
    /// boundary now carries its structured Subject and an exact EvidenceMessageId, which is
    /// what enforcement and forgetting actually need. The column is dropped.
    ///
    /// The transition-log excerpts are purged by the migration that follows this one, kept
    /// separate because SQLite implements DropColumn as a table rebuild and EF will not run
    /// arbitrary SQL and a pending rebuild in the same transaction safely.
    ///
    /// Down() restores the column shape but NOT the text, because the text is gone by
    /// intent. A privacy migration that could be reversed would not have removed anything.
    /// </summary>
    public partial class DropFrameBoundaryEvidenceStatement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvidenceStatement",
                table: "FrameBoundaries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceStatement",
                table: "FrameBoundaries",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }
    }
}
