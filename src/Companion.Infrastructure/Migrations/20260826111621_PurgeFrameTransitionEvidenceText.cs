using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <summary>
    /// R-01, the data half. Strips the user's words out of every frame transition log.
    ///
    /// Until now each entry embedded a 200-character excerpt of the turn that caused the
    /// transition, written whether or not the privacy classifier had marked that turn
    /// sensitive, and unreachable from <c>/forget</c>. Those excerpts cannot be mapped back
    /// to message ids after the fact — the id was never recorded — so they are removed
    /// rather than converted. Each entry keeps its transition kind, its timestamp and its
    /// content-safe cause; the evidence link becomes null, which is the same state a
    /// forgotten entry reaches.
    ///
    /// Kept separate from the DropColumn migration because SQLite implements a dropped
    /// column as a table rebuild, and EF cannot safely run arbitrary SQL while a rebuild is
    /// pending — it warns, and a partially applied privacy migration is the one outcome
    /// worth designing against.
    ///
    /// Idempotent: the WHERE clause means a database already free of raw evidence is left
    /// byte-for-byte untouched, so re-running costs nothing and changes nothing.
    ///
    /// Down() is deliberately empty. The wording is gone by intent; a privacy migration that
    /// could be reversed would not have removed anything.
    /// </summary>
    public partial class PurgeFrameTransitionEvidenceText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE FrameSessions
                SET TransitionLogJson = (
                    SELECT json_group_array(
                        json_object(
                            'Transition',        json_extract(value, '$.Transition'),
                            'At',                json_extract(value, '$.At'),
                            'Cause',             json_extract(value, '$.Cause'),
                            'EvidenceMessageId', json_extract(value, '$.EvidenceMessageId')))
                    FROM json_each(FrameSessions.TransitionLogJson))
                WHERE TransitionLogJson LIKE '%"Evidence"%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore. See the type comment.
        }
    }
}
