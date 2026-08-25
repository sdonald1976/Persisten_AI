using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanionMoodTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanionMoodTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousSpirits = table.Column<double>(type: "REAL", nullable: false),
                    NewSpirits = table.Column<double>(type: "REAL", nullable: false),
                    AppliedValence = table.Column<double>(type: "REAL", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanionMoodTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanionMoodTransitions_UserId_Version",
                table: "CompanionMoodTransitions",
                columns: new[] { "UserId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanionMoodTransitions");
        }
    }
}
