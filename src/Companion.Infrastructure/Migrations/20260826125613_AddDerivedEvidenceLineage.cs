using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <summary>
    /// A1. Gives derived records the exact evidence identity that makes /forget reach them.
    ///
    /// Additive for every table. New rows carry lineage from the write sites; existing rows
    /// have none, and are deliberately NOT attributed to a message they cannot be proven to
    /// come from — a wrong attribution deletes somebody else's data on the next /forget.
    ///
    /// Legacy rows are handled by authority, not uniformly:
    ///
    ///   TurnRecords are DIAGNOSTIC. Their previews, retrieval query, focal terms and
    ///   serialized plan are all derived user content that can never be forgotten correctly,
    ///   so they are purged here. Losing diagnostics costs a data point; keeping
    ///   unforgettable user text costs the promise. The content-free metrics survive.
    ///
    ///   Reflections, CompanionPreferences, KnowledgeGaps and SharedExperiencePerspectives
    ///   are live cognitive state read into the prompt on every turn. They hold Ava's own
    ///   derived commentary rather than copies of the user's wording, and purging them would
    ///   be a behaviour regression rather than a privacy improvement. They are RETAINED, and
    ///   the residual is recorded: rows written before this migration cannot be forgotten by
    ///   identity, because they never carried one.
    ///
    ///   Experiences need no legacy handling. Every existing row was written by the world
    ///   link, which has no originating message, so none of them is derived from any
    ///   forgettable evidence.
    ///
    ///   AttentionItems need none either: SourceId already held the message id.
    ///
    /// Down() restores the columns but not the purged TurnRecords content. That deletion is
    /// intentional and this migration is therefore NOT fully reversible.
    /// </summary>
    public partial class AddDerivedEvidenceLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceMessageId",
                table: "TurnRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EvidenceForgotten",
                table: "Reflections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SourceMessageIdsJson",
                table: "Reflections",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceMessageIdsJson",
                table: "KnowledgeGaps",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "EvidenceForgotten",
                table: "Experiences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceMessageId",
                table: "Experiences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EvidenceForgotten",
                table: "CompanionPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceMessageIdsJson",
                table: "CompanionPreferences",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            // Legacy diagnostic content that can never be forgotten by identity, because
            // these rows predate the identity. Guarded so a database already clean is left
            // untouched, which keeps the migration idempotent.
            migrationBuilder.Sql(
                """
                UPDATE TurnRecords
                SET UserPreview = NULL,
                    AssistantPreview = NULL,
                    RetrievalQuery = NULL,
                    Retrieved = NULL,
                    Plan = NULL,
                    FocalTerms = NULL,
                    BoundQuestion = NULL,
                    ResolvedReference = NULL
                WHERE SourceMessageId IS NULL
                  AND (UserPreview IS NOT NULL OR AssistantPreview IS NOT NULL
                       OR RetrievalQuery IS NOT NULL OR Retrieved IS NOT NULL
                       OR Plan IS NOT NULL OR FocalTerms IS NOT NULL
                       OR BoundQuestion IS NOT NULL OR ResolvedReference IS NOT NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceMessageId",
                table: "TurnRecords");

            migrationBuilder.DropColumn(
                name: "EvidenceForgotten",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "SourceMessageIdsJson",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "EvidenceMessageIdsJson",
                table: "KnowledgeGaps");

            migrationBuilder.DropColumn(
                name: "EvidenceForgotten",
                table: "Experiences");

            migrationBuilder.DropColumn(
                name: "EvidenceMessageId",
                table: "Experiences");

            migrationBuilder.DropColumn(
                name: "EvidenceForgotten",
                table: "CompanionPreferences");

            migrationBuilder.DropColumn(
                name: "EvidenceMessageIdsJson",
                table: "CompanionPreferences");
        }
    }
}
