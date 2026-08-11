using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttentionProceduresCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttentionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Strength = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastActivatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttentionItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Capabilities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    InputTypes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OutputTypes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Availability = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LocalOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    LastVerifiedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Capabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemoryAssociations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceMemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetMemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssociationType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Strength = table.Column<double>(type: "REAL", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastReinforcedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryAssociations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Procedures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Access = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Procedures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedExperiencePerspectives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ExperienceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedExperiencePerspectives", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcedureRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcedureId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcedureRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcedureRevisions_Procedures_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "Procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcedureSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcedureId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Instruction = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcedureSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcedureSteps_Procedures_ProcedureId",
                        column: x => x.ProcedureId,
                        principalTable: "Procedures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttentionItems_UserId_Status_LastActivatedAt",
                table: "AttentionItems",
                columns: new[] { "UserId", "Status", "LastActivatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryAssociations_UserId_SourceMemoryId",
                table: "MemoryAssociations",
                columns: new[] { "UserId", "SourceMemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryAssociations_UserId_TargetMemoryId",
                table: "MemoryAssociations",
                columns: new[] { "UserId", "TargetMemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureRevisions_ProcedureId",
                table: "ProcedureRevisions",
                column: "ProcedureId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureRevisions_UserId_ProcedureId_Timestamp",
                table: "ProcedureRevisions",
                columns: new[] { "UserId", "ProcedureId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureSteps_ProcedureId",
                table: "ProcedureSteps",
                column: "ProcedureId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcedureSteps_UserId_ProcedureId_Order",
                table: "ProcedureSteps",
                columns: new[] { "UserId", "ProcedureId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Procedures_UserId_Status",
                table: "Procedures",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedExperiencePerspectives_UserId_ExperienceId",
                table: "SharedExperiencePerspectives",
                columns: new[] { "UserId", "ExperienceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttentionItems");

            migrationBuilder.DropTable(
                name: "Capabilities");

            migrationBuilder.DropTable(
                name: "MemoryAssociations");

            migrationBuilder.DropTable(
                name: "ProcedureRevisions");

            migrationBuilder.DropTable(
                name: "ProcedureSteps");

            migrationBuilder.DropTable(
                name: "SharedExperiencePerspectives");

            migrationBuilder.DropTable(
                name: "Procedures");
        }
    }
}
