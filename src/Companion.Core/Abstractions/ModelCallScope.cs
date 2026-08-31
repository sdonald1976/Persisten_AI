namespace Companion.Core.Abstractions;

/// <summary>One model invocation seen by the logging seam: which role, which model answered.</summary>
public sealed record ModelCall(string Role, string? Model, bool Ok);

/// <summary>
/// A per-turn ledger of every chat-model call, kept in an AsyncLocal so it follows the turn's
/// async flow without threading a parameter through fifteen layers.
///
/// This exists to make "zero Stheno calls on this turn" a MEASURED claim instead of an
/// architectural hope. The Stheno-free route's tests and its turn diagnostics both read this
/// ledger; the recording site is the same <c>LoggingChatModel</c> decorator every role already
/// passes through, so a call cannot dodge the ledger without also dodging telemetry.
///
/// Fire-and-forget work started inside the scope (shadow observation, post-turn extraction)
/// inherits the ExecutionContext and therefore records into the same ledger - which is wanted:
/// a background call to the conversational model is still a call to it.
/// </summary>
public static class ModelCallScope
{
    private sealed class Ledger
    {
        public readonly List<ModelCall> Calls = [];
        public readonly object Lock = new();
    }

    private static readonly AsyncLocal<Ledger?> Current = new();

    /// <summary>Opens a ledger for this async flow. Dispose to detach (calls stay readable).</summary>
    public static IDisposable Open()
    {
        var ledger = new Ledger();
        Current.Value = ledger;
        return new Closer(ledger);
    }

    /// <summary>Records a call if a ledger is open; free when none is.</summary>
    public static void Record(string role, string? model, bool ok)
    {
        if (Current.Value is not { } ledger)
            return;
        lock (ledger.Lock)
            ledger.Calls.Add(new ModelCall(role, model, ok));
    }

    /// <summary>Every call recorded so far in this flow's ledger.</summary>
    public static IReadOnlyList<ModelCall> Snapshot()
    {
        if (Current.Value is not { } ledger)
            return [];
        lock (ledger.Lock)
            return ledger.Calls.ToArray();
    }

    private sealed class Closer(Ledger ledger) : IDisposable
    {
        public void Dispose()
        {
            // Detach only if this flow still points at OUR ledger - an inner scope that
            // opened later already replaced it and owns the cleanup of its own.
            if (ReferenceEquals(Current.Value, ledger))
                Current.Value = null;
        }
    }
}
