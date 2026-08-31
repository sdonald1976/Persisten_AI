using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Companion.Core.Domain;

/// <summary>What kind of conversational move is left hanging at the end of a turn.</summary>
public enum PendingMoveKind
{
    /// <summary>A question needing an answer ("does Thursday work?").</summary>
    Question,

    /// <summary>An invitation or proposal awaiting acceptance ("want me to dig that up?").</summary>
    Invitation,

    /// <summary>A request for clarification ("which one do you mean?").</summary>
    Clarification,

    /// <summary>A promise to explain or provide something not yet supplied.</summary>
    Promise,
}

/// <summary>How the user's next message related to the pending move, decided by the existing
/// typed understanding (answer binding, correction detection) - never by phrase matching here.</summary>
public enum MoveResolution { Accepted, Rejected, Answered, Redirected }

/// <summary>
/// The conversational move a turn left open: Ava asked, offered, or promised something, and
/// the next user message will land against it. One at a time, deliberately - conversation has
/// one "ball in the air", and the newest ask supersedes an older unanswered one exactly the
/// way it does between people.
/// </summary>
public sealed record PendingMove
{
    public required PendingMoveKind Kind { get; init; }

    /// <summary>The move's text as displayed (the question asked, the offer made).</summary>
    public required string Text { get; init; }

    /// <summary>Semantic identity - see <see cref="MoveIdentity.Of"/>. Repetition detection
    /// compares THIS, so a rephrasing of the same ask still counts as the same move.</summary>
    public required string Identity { get; init; }

    /// <summary>The plan item that carried it, when one did.</summary>
    public string? PlanItemId { get; init; }
}

/// <summary>
/// Semantic move identity: the ordered set of content words, hashed. "Ready for something a
/// bit more adventurous?" and "ready for something more adventurous" collide (same move);
/// "ready for lunch?" does not. Deliberately not literal string equality and deliberately not
/// a model call - repetition suppression must be deterministic to be testable.
/// </summary>
public static partial class MoveIdentity
{
    [GeneratedRegex(@"[\p{L}\p{Nd}][\p{L}\p{Nd}'-]*")]
    private static partial Regex Words();

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "was", "are", "be", "been", "of", "to", "in", "on", "at",
        "for", "and", "or", "but", "it", "that", "this", "with", "as", "by", "from", "you",
        "your", "i", "my", "we", "me", "they", "there", "here", "just", "so", "then", "now",
        "if", "when", "what", "how", "why", "do", "does", "did", "bit", "little", "some",
        "something", "anything", "more", "very", "really", "quite", "ready", "want", "would",
    };

    public static string Of(string text)
    {
        var tokens = Words().Matches(text.Replace('’', '\''))
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 2 && !Stop.Contains(w))
            .Distinct()
            .OrderBy(w => w, StringComparer.Ordinal)
            .ToList();
        var canonical = string.Join(' ', tokens);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]
            .ToLowerInvariant();
    }
}

/// <summary>
/// Per-conversation move state: the pending move, and the identities of moves already
/// satisfied (answered/accepted) this conversation, so no satisfied move is ever re-issued
/// unless the user explicitly returns to it.
/// </summary>
public interface IConversationMoveStore
{
    PendingMove? GetPending(Guid conversationId);

    /// <summary>Records the move this turn left open (or clears it when the turn left none).</summary>
    void SetPending(Guid conversationId, PendingMove? move);

    /// <summary>Marks a move satisfied. It stays suppressed for the rest of the conversation.</summary>
    void MarkSatisfied(Guid conversationId, string identity);

    bool IsSatisfied(Guid conversationId, string identity);

    /// <summary>Snapshot of every satisfied move identity in this conversation.</summary>
    IReadOnlyCollection<string> SatisfiedIdentities(Guid conversationId);
}

/// <summary>
/// In-process move state, one entry per conversation. In-memory on purpose for now: the move
/// a restart forgets is a move the user can simply make again, and durable storage would need
/// the same privacy treatment as messages for marginal benefit.
/// </summary>
public sealed class InMemoryConversationMoveStore : IConversationMoveStore
{
    private sealed class State
    {
        public PendingMove? Pending;
        public readonly HashSet<string> Satisfied = new(StringComparer.Ordinal);
    }

    private readonly object _lock = new();
    private readonly Dictionary<Guid, State> _byConversation = [];

    public PendingMove? GetPending(Guid conversationId)
    {
        lock (_lock)
            return _byConversation.TryGetValue(conversationId, out var s) ? s.Pending : null;
    }

    public void SetPending(Guid conversationId, PendingMove? move)
    {
        lock (_lock)
            Get(conversationId).Pending = move;
    }

    public void MarkSatisfied(Guid conversationId, string identity)
    {
        lock (_lock)
        {
            var s = Get(conversationId);
            s.Satisfied.Add(identity);
            if (s.Pending?.Identity == identity)
                s.Pending = null;
        }
    }

    public bool IsSatisfied(Guid conversationId, string identity)
    {
        lock (_lock)
            return _byConversation.TryGetValue(conversationId, out var s)
                   && s.Satisfied.Contains(identity);
    }

    public IReadOnlyCollection<string> SatisfiedIdentities(Guid conversationId)
    {
        lock (_lock)
            return _byConversation.TryGetValue(conversationId, out var s)
                ? s.Satisfied.ToArray() : [];
    }

    private State Get(Guid conversationId)
        => _byConversation.TryGetValue(conversationId, out var s)
            ? s : _byConversation[conversationId] = new State();
}
