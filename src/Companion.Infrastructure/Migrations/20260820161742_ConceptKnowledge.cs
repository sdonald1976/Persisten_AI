using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConceptKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConceptAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConceptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Alias = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptAliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConceptAssertions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConceptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Relation = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetConceptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Value = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    NormalizedText = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Importance = table.Column<double>(type: "REAL", nullable: false),
                    Validity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    FirstObserved = table.Column<long>(type: "INTEGER", nullable: false),
                    LastConfirmed = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConceptAssertions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Concepts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CanonicalName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Concepts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptAliases_UserId_Alias",
                table: "ConceptAliases",
                columns: new[] { "UserId", "Alias" });

            migrationBuilder.CreateIndex(
                name: "IX_ConceptAssertions_UserId_ConceptId",
                table: "ConceptAssertions",
                columns: new[] { "UserId", "ConceptId" });

            migrationBuilder.CreateIndex(
                name: "IX_Concepts_UserId_CanonicalName",
                table: "Concepts",
                columns: new[] { "UserId", "CanonicalName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConceptAliases");

            migrationBuilder.DropTable(
                name: "ConceptAssertions");

            migrationBuilder.DropTable(
                name: "Concepts");
        }
    }
}
