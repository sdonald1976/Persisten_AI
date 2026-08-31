using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <summary>
    /// Additive: one nullable TEXT column holding the per-turn memory-provenance trace as JSON.
    /// Nullable so no historical row needs backfilling and existing consumers are untouched.
    /// </summary>
    public partial class AddTurnRecordMemoryProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MemoryProvenance",
                table: "TurnRecords",
                type: "TEXT",
                maxLength: 20000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryProvenance",
                table: "TurnRecords");
        }
    }
}
