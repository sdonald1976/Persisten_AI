using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Infrastructure.Seeding;

/// <summary>
/// Seeds a small, fixed slice of fictional multi-month history so the vertical slice is
/// demonstrable and tests are deterministic. It deliberately includes:
///   - an open loop (planned Jetson home-test) for continuity (Scenario A);
///   - a historical fact (old Raspberry Pi) to exercise "possibly outdated" labeling (Scenario B);
///   - a second hardware project (buoy sensor board) to create genuine ambiguity (Scenario C).
/// Later phases grow this dataset.
/// </summary>
public sealed class CompanionSeeder
{
    public const string DemoUserId = "demo-user";
    public const string JetsonProject = "Jetson Nano object detection";
    public const string BuoyProject = "buoy sensor platform";

    private readonly IConversationStore _conversations;
    private readonly IMemoryStore _memories;
    private readonly IProjectStore _projects;
    private readonly IEmbeddingModel _embeddings;

    public CompanionSeeder(
        IConversationStore conversations,
        IMemoryStore memories,
        IProjectStore projects,
        IEmbeddingModel embeddings)
    {
        _conversations = conversations;
        _memories = memories;
        _projects = projects;
        _embeddings = embeddings;
    }

    /// <summary>Seeds the demo user. Returns false (no-op) if already seeded.</summary>
    public async Task<bool> SeedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var existing = await _memories.GetRetrievableMemoriesAsync(DemoUserId, ct);
        if (existing.Count > 0)
            return false;

        // --- A conversation from ~5 months ago about the Jetson deployment ---
        var jetsonConv = await _conversations.StartConversationAsync(
            DemoUserId, "Jetson deployment", modelUsed: "mock", source: "seed", ct);

        var jetsonMsg = await AddUserMessageAsync(
            jetsonConv.Id,
            "I deployed the object detection service to a Jetson Nano, but I haven't tested it at home yet. " +
            "I moved off the Raspberry Pi 3 because it was too slow.",
            now.AddMonths(-5), ct);

        await AddEpisodicAsync(new EpisodicMemory
        {
            Description = "Deployed the object-detection service to a Jetson Nano.",
            EventTime = now.AddMonths(-5),
            TimePrecision = TimePrecision.Day,
            MentionedAt = now.AddMonths(-5),
            EpisodeStatus = EpisodeStatus.Resolved,
            Importance = 0.7,
            Confidence = 0.95,
            RelatedProject = JetsonProject,
        }, jetsonMsg, "deployed the object detection service to a Jetson Nano", now, ct);

        await AddEpisodicAsync(new EpisodicMemory
        {
            Description = "Planned to test the Jetson Nano object-detection deployment at home.",
            EventTime = now.AddMonths(-5),
            TimePrecision = TimePrecision.Day,
            MentionedAt = now.AddMonths(-5),
            EpisodeStatus = EpisodeStatus.Planned, // open loop
            Importance = 0.8,
            Confidence = 0.9,
            RelatedProject = JetsonProject,
        }, jetsonMsg, "I haven't tested it at home yet", now, ct);

        await AddSemanticAsync(new SemanticMemory
        {
            Subject = "user",
            Predicate = "uses",
            Value = "a Raspberry Pi 3 for the object-detection prototype",
            NormalizedFact = "Used a Raspberry Pi 3 for the object-detection prototype (later replaced by a Jetson Nano).",
            Confidence = 0.85,
            Importance = 0.4,
            Validity = Validity.Historical, // superseded by the Jetson; shown as possibly-outdated
            RelatedProject = JetsonProject,
        }, jetsonMsg, "I moved off the Raspberry Pi 3", now.AddMonths(-5), now, ct);

        await AddSemanticAsync(new SemanticMemory
        {
            Subject = "user",
            Predicate = "interested_in",
            Value = "embedded computing and edge-AI projects",
            NormalizedFact = "The user works on embedded-computing and edge-AI hardware projects.",
            Confidence = 0.8,
            Importance = 0.6,
            Validity = Validity.Current,
        }, jetsonMsg, "Jetson Nano ... object detection service", now.AddMonths(-5), now, ct);

        // --- A conversation from ~2 months ago about the buoy project (ambiguity source) ---
        var buoyConv = await _conversations.StartConversationAsync(
            DemoUserId, "Buoy sensor board", modelUsed: "mock", source: "seed", ct);

        var buoyMsg = await AddUserMessageAsync(
            buoyConv.Id,
            "I ordered a custom sensor board for the buoy project. Still waiting for it to arrive.",
            now.AddMonths(-2), ct);

        await AddEpisodicAsync(new EpisodicMemory
        {
            Description = "Ordered a custom sensor board for the buoy project; awaiting delivery.",
            EventTime = now.AddMonths(-2),
            TimePrecision = TimePrecision.Day,
            MentionedAt = now.AddMonths(-2),
            EpisodeStatus = EpisodeStatus.Planned, // open loop: awaiting arrival
            Importance = 0.6,
            Confidence = 0.9,
            RelatedProject = BuoyProject,
        }, buoyMsg, "ordered a custom sensor board for the buoy project", now, ct);

        // --- A durable preference (continuity flavor) ---
        var cookConv = await _conversations.StartConversationAsync(
            DemoUserId, "Cooking", modelUsed: "mock", source: "seed", ct);
        var cookMsg = await AddUserMessageAsync(
            cookConv.Id,
            "When you give me recipes, I like step-by-step instructions with exact temperatures and timing.",
            now.AddMonths(-3), ct);

        await AddSemanticAsync(new SemanticMemory
        {
            Subject = "user",
            Predicate = "prefers",
            Value = "step-by-step cooking instructions with exact temperatures and timing",
            NormalizedFact = "The user prefers step-by-step cooking instructions with exact temperatures and timing.",
            Confidence = 0.75,
            Importance = 0.5,
            Validity = Validity.Current,
        }, cookMsg, "step-by-step instructions with exact temperatures and timing", now.AddMonths(-3), now, ct);

        // --- First-class projects (Phase 4) ---
        // Both hardware projects share the alias "board" so an oblique "that board..." is
        // genuinely ambiguous and the resolver asks to clarify.
        var jetson = await AddProjectAsync(
            JetsonProject,
            "Edge object-detection service deployed to a Jetson Nano.",
            new[] { "Jetson", "the Jetson project", "object detection", "board" },
            now.AddMonths(-5), now.AddMonths(-5), ct);

        await AddDecisionAsync(jetson.Id,
            "Moved from a Raspberry Pi 3 to a Jetson Nano for the object detector.",
            "The Pi 3 was too slow for real-time inference.", jetsonMsg.Id, now.AddMonths(-5), ct);
        await AddProjectEventAsync(jetson.Id, ProjectEventKind.Milestone,
            "Deployed the object-detection service to the Jetson Nano.", jetsonMsg.Id, now.AddMonths(-5), ct);
        await AddOpenLoopAsync(jetson.Id,
            "Test the Jetson Nano object-detection deployment at home.", jetsonMsg.Id, now.AddMonths(-5), ct);

        var buoy = await AddProjectAsync(
            BuoyProject,
            "A sensor platform for an ocean buoy.",
            new[] { "buoy", "the buoy project", "sensor board", "board" },
            now.AddMonths(-2), now.AddMonths(-2), ct);

        await AddProjectEventAsync(buoy.Id, ProjectEventKind.Note,
            "Ordered a custom sensor board for the buoy.", buoyMsg.Id, now.AddMonths(-2), ct);
        await AddOpenLoopAsync(buoy.Id,
            "Custom sensor board for the buoy — awaiting delivery.", buoyMsg.Id, now.AddMonths(-2), ct);

        return true;
    }

    private async Task<Project> AddProjectAsync(
        string name, string purpose, string[] aliases,
        DateTimeOffset createdAt, DateTimeOffset lastActivity, CancellationToken ct)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = DemoUserId,
            Name = name,
            Purpose = purpose,
            Status = ProjectStatus.Active,
            CreatedAt = createdAt,
            LastActivityAt = lastActivity,
            Embedding = await _embeddings.EmbedAsync($"{name} {string.Join(' ', aliases)} {purpose}", ct),
        };
        await _projects.AddProjectAsync(project, ct);

        foreach (var alias in aliases)
            await _projects.AddAliasAsync(new ProjectAlias
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                UserId = DemoUserId,
                Alias = alias,
                Source = "seed",
                Confidence = 0.9,
            }, ct);

        return project;
    }

    private Task AddDecisionAsync(
        Guid projectId, string statement, string rationale, Guid sourceMessageId,
        DateTimeOffset decidedAt, CancellationToken ct)
        => _projects.AddDecisionAsync(new Decision
        {
            Id = Guid.NewGuid(),
            UserId = DemoUserId,
            ProjectId = projectId,
            Statement = statement,
            Rationale = rationale,
            DecidedAt = decidedAt,
            Status = MemoryStatus.Active,
            SourceMessageId = sourceMessageId,
        }, ct);

    private Task AddProjectEventAsync(
        Guid projectId, ProjectEventKind kind, string description, Guid sourceMessageId,
        DateTimeOffset at, CancellationToken ct)
        => _projects.AddEventAsync(new ProjectEvent
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            UserId = DemoUserId,
            Kind = kind,
            Description = description,
            Timestamp = at,
            SourceMessageId = sourceMessageId,
        }, ct);

    private async Task AddOpenLoopAsync(
        Guid projectId, string description, Guid sourceMessageId, DateTimeOffset createdAt, CancellationToken ct)
        => await _projects.AddOpenLoopAsync(new OpenLoop
        {
            Id = Guid.NewGuid(),
            UserId = DemoUserId,
            ProjectId = projectId,
            Description = description,
            Owner = "user",
            Status = OpenLoopStatus.Open,
            CreatedAt = createdAt,
            SourceMessageId = sourceMessageId,
            Embedding = await _embeddings.EmbedAsync(description, ct),
        }, ct);

    private async Task<Message> AddUserMessageAsync(
        Guid conversationId, string content, DateTimeOffset at, CancellationToken ct)
    {
        var msg = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = DemoUserId,
            Role = MessageRole.User,
            Content = content,
            Timestamp = at,
        };
        await _conversations.AddMessageAsync(msg, ct);
        return msg;
    }

    private async Task AddEpisodicAsync(
        EpisodicMemory memory, Message source, string excerpt, DateTimeOffset createdAt, CancellationToken ct)
    {
        memory.Id = Guid.NewGuid();
        memory.UserId = DemoUserId;
        memory.CreatedAt = createdAt;
        memory.Status = MemoryStatus.Active;
        memory.Embedding = await _embeddings.EmbedAsync(memory.Description, ct);
        memory.Evidence.Add(new MemoryEvidence { MessageId = source.Id, Excerpt = excerpt, Weight = 1.0 });
        await _memories.AddEpisodicAsync(memory, ct);
    }

    private async Task AddSemanticAsync(
        SemanticMemory memory, Message source, string excerpt,
        DateTimeOffset firstObserved, DateTimeOffset createdAt, CancellationToken ct)
    {
        memory.Id = Guid.NewGuid();
        memory.UserId = DemoUserId;
        memory.FirstObserved = firstObserved;
        memory.LastConfirmed = firstObserved;
        memory.CreatedAt = createdAt;
        memory.Status = MemoryStatus.Active;
        memory.Embedding = await _embeddings.EmbedAsync(memory.NormalizedFact, ct);
        memory.Evidence.Add(new MemoryEvidence { MessageId = source.Id, Excerpt = excerpt, Weight = 1.0 });
        await _memories.AddSemanticAsync(memory, ct);
    }
}
