namespace Companion.Core.Domain;

/// <summary>
/// Thrown when an operation references a conversation that does not exist or is not owned by the
/// acting user. Callers (e.g. the API) map this to a 404 — deliberately not distinguishing
/// "missing" from "belongs to someone else", so a foreign conversation's existence never leaks.
/// A missing conversation is an <em>invalid request</em>, never a silently-non-private one.
/// </summary>
public sealed class ConversationNotFoundException : Exception
{
    public Guid ConversationId { get; }

    public ConversationNotFoundException(Guid conversationId)
        : base($"Conversation {conversationId} was not found for the current user.")
        => ConversationId = conversationId;
}
