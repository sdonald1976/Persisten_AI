using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShadowComparisons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShadowComparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Legacy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Agreed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Applied = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Input = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShadowComparisons", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShadowComparisons_Subject_Timestamp",
                table: "ShadowComparisons",
                columns: new[] { "Subject", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShadowComparisons");
        }
    }
}
