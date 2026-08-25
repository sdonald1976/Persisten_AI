using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoodCompactionBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "PreviousSpirits",
                table: "CompanionMoodTransitions",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AddColumn<long>(
                name: "CompactedAt",
                table: "CompanionMoodTransitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBaseline",
                table: "CompanionMoodTransitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompactedAt",
                table: "CompanionMoodTransitions");

            migrationBuilder.DropColumn(
                name: "IsBaseline",
                table: "CompanionMoodTransitions");

            migrationBuilder.AlterColumn<double>(
                name: "PreviousSpirits",
                table: "CompanionMoodTransitions",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
