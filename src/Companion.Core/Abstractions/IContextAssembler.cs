using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>Builds a bounded, labeled context packet from retrieved memories and recent messages.</summary>
public interface IContextAssembler
{
    ContextPacket Assemble(
        string userMessage,
        IReadOnlyList<Message> recentMessages,
        IReadOnlyList<RetrievalResult> retrieved);
}
