using System.Collections.Concurrent;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>Per-user ring buffer of the last few turns' diagnostics. Singleton; process-lifetime only.</summary>
public sealed class InMemoryTurnTraceLog : ITurnTraceLog
{
    private const int Keep = 5;

    private readonly ConcurrentDictionary<string, LinkedList<TurnDiagnostics>> _turns = new();

    public void Record(string userId, TurnDiagnostics diagnostics)
    {
        var list = _turns.GetOrAdd(userId, _ => new LinkedList<TurnDiagnostics>());
        lock (list)
        {
            list.AddFirst(diagnostics);
            while (list.Count > Keep)
                list.RemoveLast();
        }
    }

    public IReadOnlyList<TurnDiagnostics> Recent(string userId, int count)
    {
        if (!_turns.TryGetValue(userId, out var list))
            return Array.Empty<TurnDiagnostics>();
        lock (list)
        {
            return list.Take(Math.Max(1, count)).ToList();
        }
    }
}
