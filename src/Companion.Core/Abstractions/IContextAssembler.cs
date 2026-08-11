using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Builds a bounded, labeled context packet from retrieved memories, project state, and recent messages.</summary>
public interface IContextAssembler
{
    ContextPacket Assemble(
        string userMessage,
        IReadOnlyList<Message> recentMessages,
        IReadOnlyList<RetrievalResult> retrieved,
        ProjectContext projectContext,
        string? persona = null,
        RelationshipSnapshot? relationship = null,
        string? musing = null,
        string? curiosity = null,
        string? companionMood = null);
}
