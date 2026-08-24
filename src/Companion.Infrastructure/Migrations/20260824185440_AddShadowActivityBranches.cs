using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShadowActivityBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    BranchId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentBranchId = table.Column<string>(type: "TEXT", nullable: true),
                    BranchPointQuestionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    BranchKind = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    ProcedureDefinitionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActivityType = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    QuestionLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentQuestionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    MovesJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnswerBindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HypothesesJson = table.Column<string>(type: "TEXT", nullable: true),
                    FinalGuess = table.Column<string>(type: "TEXT", nullable: true),
                    FinalGuessCorrect = table.Column<bool>(type: "INTEGER", nullable: true),
                    AppliedKeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActivationEvidence = table.Column<string>(type: "TEXT", nullable: true),
                    Retention = table.Column<string>(type: "TEXT", nullable: false),
                    ContentWithheld = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TerminalAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityBranches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityBranches_BranchId",
                table: "ActivityBranches",
                column: "BranchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityBranches_TerminalAt",
                table: "ActivityBranches",
                column: "TerminalAt");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityBranches_UserId_ConversationId",
                table: "ActivityBranches",
                columns: new[] { "UserId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityBranches");
        }
    }
}
