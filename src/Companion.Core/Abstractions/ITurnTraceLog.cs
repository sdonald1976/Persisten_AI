using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// A short in-memory ring of recent turn diagnostics per user — the substrate for
/// "why did you say that?" introspection (the diagnostics.last_turn tool). Deliberately not
/// persistence: operational self-knowledge, not memory, and never a place for secrets.
/// </summary>
public interface ITurnTraceLog
{
    void Record(string userId, TurnDiagnostics diagnostics);

    /// <summary>Most recent turns, newest first, capped at <paramref name="count"/>.</summary>
    IReadOnlyList<TurnDiagnostics> Recent(string userId, int count);
}
