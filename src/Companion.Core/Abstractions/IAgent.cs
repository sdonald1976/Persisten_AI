using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// The companion "brain" as a single facade: understands an utterance, then either runs a
/// conversational turn or carries out a recognized intent — returning structured
/// <see cref="AgentReply"/> data rather than printing anything. Every face (CLI, HTTP, voice,
/// 3D avatar) drives the companion through this one surface, so behavior stays identical across them.
/// </summary>
public interface IAgent
{
    /// <summary>
    /// Handle one user utterance. Plain conversation runs a full turn (streamed to
    /// <paramref name="tokenSink"/> when provided); recognized intents carry out an action.
    /// Destructive intents return <see cref="AgentReplyKind.Confirmation"/> instead of acting.
    /// </summary>
    Task<AgentReply> HandleAsync(
        string userId,
        Guid conversationId,
        string input,
        IProgress<string>? tokenSink = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve a pending confirmation from a prior <see cref="AgentReplyKind.Confirmation"/> reply.
    /// </summary>
    Task<AgentReply> ConfirmAsync(
        string userId,
        Guid conversationId,
        string confirmationToken,
        bool confirmed,
        CancellationToken ct = default);
}
