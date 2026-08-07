using System.Text.Json;
using System.Text.Json.Serialization;
using Companion.Core.Domain;

namespace Companion.Api;

// ---- request bodies ----
// Note: requests never carry a user id. The active user is derived from the server's trusted
// IUserContext, never from the request body, so a caller cannot act as another user.

public sealed record ChatRequest(string ConversationId, string Message);

public sealed record ConfirmRequest(string ConversationId, string ConfirmationToken, bool Confirmed);

public sealed record StartConversationRequest(string? Title, string? Source);

public sealed record PersonaRequest(string? Persona);

public sealed record PersonalityRequest(string? Preset);

public sealed record FeedbackRequest(string ConversationId, string Rating, string? Note);

// ---- responses ----

/// <summary>Wire form of <see cref="AgentReply"/> — what the brain decided to say/do.</summary>
public sealed record ReplyDto(
    string Kind,
    string Intent,
    string Text,
    string? ConfirmationToken,
    TraceDto? Trace)
{
    public static ReplyDto From(AgentReply reply) => new(
        reply.Kind.ToString(),
        reply.Intent.ToString(),
        reply.Text,
        reply.ConfirmationToken,
        reply.Trace is null ? null : TraceDto.From(reply.Trace));
}

/// <summary>A compact, UI-friendly slice of <see cref="TurnTrace"/> (the full packet stays server-side).</summary>
public sealed record TraceDto(
    string? DetectedProject,
    IReadOnlyList<RetrievedDto> Retrieved,
    int MemoriesExtracted,
    int OpenLoopsSurfaced)
{
    public static TraceDto From(TurnTrace t) => new(
        t.DetectedProject,
        t.Retrieved.Select(r => new RetrievedDto(r.Memory.Content, Math.Round(r.Score, 3))).ToList(),
        t.Extraction.Accepted + t.Extraction.Merged,
        t.ProjectContext.OpenLoops.Count);
}

public sealed record RetrievedDto(string Content, double Score);

public sealed record MemoryDto(string Id, string Kind, string Content, string Status, string? Validity, double Confidence)
{
    public static MemoryDto From(IMemory m) => new(
        m.Id.ToString(),
        m.Kind.ToString(),
        m.Content,
        m.Status.ToString(),
        m is SemanticMemory sm ? sm.Validity.ToString() : null,
        Math.Round(m.Confidence, 3));
}

public sealed record ProjectDto(string Name, string Status, string? Purpose)
{
    public static ProjectDto From(Project p) => new(p.Name, p.Status.ToString(), p.Purpose);
}

public sealed record OpenLoopDto(string Id, string Description)
{
    public static OpenLoopDto From(OpenLoop l) => new(l.Id.ToString(), l.Description);
}

/// <summary>Shared JSON settings for the hand-serialized SSE/WebSocket frames.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
