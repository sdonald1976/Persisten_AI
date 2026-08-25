using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Dimension = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Restrictive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SupersededById = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActiveSlot = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    StatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeactivatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    EvidenceEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvidenceKind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EvidenceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EvidenceStatement = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevocationEvidenceMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RevocationStatement = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_ActiveSlot",
                table: "UserPreferences",
                columns: new[] { "UserId", "ActiveSlot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_Status",
                table: "UserPreferences",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPreferences");
        }
    }
}
