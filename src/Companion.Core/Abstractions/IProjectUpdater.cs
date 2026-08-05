using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Step 10 of the turn: after memories are accepted, reflect them into project state —
/// open a loop for a newly-planned matter, close one when a matching item is reported done,
/// and log project activity. Kept separate from extraction so the concerns stay distinct.
/// </summary>
public interface IProjectUpdater
{
    Task<ProjectUpdateResult> ApplyAsync(
        string userId,
        IReadOnlyList<Message> exchange,
        MemoryExtractionResult extraction,
        ProjectContext projectContext,
        CancellationToken ct = default);
}
