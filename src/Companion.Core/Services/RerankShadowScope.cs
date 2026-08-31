namespace Companion.Core.Services;

/// <summary>
/// Carries the current turn's identity and record-permission into the reranker, which sits deep
/// inside retrieval and otherwise knows neither. An AsyncLocal, opened once per turn by the
/// orchestrator, so the reranker shadow can attribute a comparison to a turn and honour the same
/// private-turn exclusion the rest of the pipeline uses — without threading parameters through
/// every retrieval signature.
///
/// When no scope is open (background retrieval, tests) or the turn is private, the shadow simply
/// does not record; the authoritative reranker runs exactly as before either way.
/// </summary>
public static class RerankShadowScope
{
    private sealed record State(Guid TurnId, string UserId, bool MayRecord);

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>Opens a scope for this turn. <paramref name="mayRecord"/> is false on private/
    /// sensitive turns, exactly like the derived-memory and provenance exclusions.</summary>
    public static IDisposable Open(Guid turnId, string userId, bool mayRecord)
    {
        Current.Value = new State(turnId, userId, mayRecord);
        return new Closer(Current.Value);
    }

    public static bool ShouldRecord => Current.Value is { MayRecord: true };

    public static (Guid TurnId, string UserId)? Turn =>
        Current.Value is { } s ? (s.TurnId, s.UserId) : null;

    private sealed class Closer(object token) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, token))
                Current.Value = null;
        }
    }
}
