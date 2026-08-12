using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Core.Services.Tools;

/// <summary>Shared argument plumbing for the read-only tool set.</summary>
internal static class ToolArgs
{
    public static string? Text(JsonElement args, string name, int maxLength)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public static int Limit(JsonElement args, string name, int fallback, int max)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return Math.Clamp(n, 1, max);
        return fallback;
    }

    public static string Clip(string? text, int max)
        => string.IsNullOrEmpty(text) ? "" : text!.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// capability.list — her honest self-knowledge: what THIS installation can actually do right now,
/// straight from the capability registry (availability, provider, last verified), plus the tools
/// she can invoke. Never claims what isn't wired up.
/// </summary>
public sealed class CapabilityListTool : ICompanionTool
{
    private readonly ICapabilityRegistry _registry;
    private readonly IServiceProvider _services;

    // The tool set is resolved lazily at execution time: this tool is itself a member of that
    // set, and resolving IEnumerable<ICompanionTool> in the constructor would be a cycle.
    public CapabilityListTool(ICapabilityRegistry registry, IServiceProvider services)
    {
        _registry = registry;
        _services = services;
    }

    public string Name => "capability.list";
    public string Description =>
        "List what you can ACTUALLY do in this installation — capabilities with live availability, " +
        "and the tools you can call. Use when asked \"what can you do / can you see images / can you hear me?\".";
    public string ArgumentsHint => "{} (no arguments)";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var capabilities = await _registry.GetAllAsync(ct);
        return ToolResult.Success(new
        {
            capabilities = capabilities.Select(c => new
            {
                c.Id,
                c.Name,
                available = c.Availability.ToString(),
                c.Provider,
                lastVerifiedAt = c.LastVerifiedAt,
            }),
            tools = _services.GetServices<ICompanionTool>()
                .Where(t => t.Available).Select(t => new { t.Name, t.Description }),
        });
    }
}

/// <summary>
/// memory.search — deliberate recall when automatic retrieval wasn't enough. Runs the SAME
/// hybrid ranked retrieval (with reranking) the turn uses, scoped to the trusted user, returning
/// bounded structured results with provenance labels.
/// </summary>
public sealed class MemorySearchTool : ICompanionTool
{
    private readonly IRetriever _retriever;

    public MemorySearchTool(IRetriever retriever) => _retriever = retriever;

    public string Name => "memory.search";
    public string Description =>
        "Deliberately search everything you remember (facts, events, shared moments) for a topic — " +
        "use when the user references something old that the current context doesn't cover.";
    public string ArgumentsHint => "{\"query\": \"what to look for\", \"limit\": \"1-8 (optional)\"}";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var query = ToolArgs.Text(arguments, "query", 200);
        if (query is null)
            return ToolResult.Fail("invalid_arguments", "Provide a non-empty \"query\" string.");
        var limit = ToolArgs.Limit(arguments, "limit", fallback: 5, max: 8);

        var outcome = await _retriever.RetrieveAsync(userId, query, detectedProject: null, ct);
        return ToolResult.Success(new
        {
            query,
            results = outcome.Selected.Take(limit).Select(r => new
            {
                summary = ToolArgs.Clip(r.Memory.Content, 200),
                kind = r.Memory.Kind.ToString(),
                owner = r.Memory.Owner.ToString(),
                confidence = Math.Round(r.Memory.Confidence, 2),
                why = r.Reason,
            }),
        });
    }
}

/// <summary>
/// project.get — look into a project by name (summary, decisions, open loops, recent activity),
/// or list all projects when no name is given.
/// </summary>
public sealed class ProjectTool : ICompanionTool
{
    private readonly IProjectStore _projects;
    private readonly IEntityResolver _resolver;
    private readonly IProjectContextService _context;

    public ProjectTool(IProjectStore projects, IEntityResolver resolver, IProjectContextService context)
    {
        _projects = projects;
        _resolver = resolver;
        _context = context;
    }

    public string Name => "project.get";
    public string Description =>
        "Look up one of the user's projects by name (summary, decisions, open loops, recent activity) — " +
        "or list all projects when called without a name.";
    public string ArgumentsHint => "{\"name\": \"project name (optional — omit to list all)\"}";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var name = ToolArgs.Text(arguments, "name", 200);
        if (name is null)
        {
            var all = await _projects.GetProjectsAsync(userId, ct);
            return ToolResult.Success(new
            {
                projects = all.OrderByDescending(p => p.LastActivityAt).Take(10)
                    .Select(p => new { p.Name, status = p.Status.ToString() }),
            });
        }

        var resolution = await _resolver.ResolveProjectAsync(userId, name, ct);
        if (resolution.Best is null)
            return ToolResult.Fail("not_found", $"No project matching \"{name}\".");

        var summary = await _context.GetSummaryAsync(userId, resolution.Best.Project.Id, ct);
        if (summary is null)
            return ToolResult.Fail("not_found", "The project has no summary.");

        return ToolResult.Success(new
        {
            name = summary.Project.Name,
            status = summary.Project.Status.ToString(),
            purpose = summary.Project.Purpose,
            decisions = summary.Decisions.Take(5).Select(d => ToolArgs.Clip(d.Statement, 150)),
            openLoops = summary.OpenLoops.Take(5).Select(l => ToolArgs.Clip(l.Description, 150)),
            recentActivity = summary.RecentEvents.Take(5).Select(e => ToolArgs.Clip(e.Description, 150)),
        });
    }
}

/// <summary>openloop.list — everything unfinished she's holding: loops, dated events, wonderings.</summary>
public sealed class OpenLoopListTool : ICompanionTool
{
    private readonly IProjectStore _projects;
    private readonly IAnticipationStore _anticipations;
    private readonly IReflectionStore _reflections;

    public OpenLoopListTool(IProjectStore projects, IAnticipationStore anticipations, IReflectionStore reflections)
    {
        _projects = projects;
        _anticipations = anticipations;
        _reflections = reflections;
    }

    public string Name => "openloop.list";
    public string Description =>
        "List everything unfinished you're holding: open loops, upcoming/passed dated events, and " +
        "your own open questions. Use when wondering what's unresolved between you.";
    public string ArgumentsHint => "{} (no arguments)";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var loops = await _projects.GetOpenLoopsAsync(userId, onlyOpen: true, ct);
        var anticipations = await _anticipations.GetOpenAsync(userId, ct);
        var curiosities = await _reflections.GetOpenCuriositiesAsync(userId, ct);

        return ToolResult.Success(new
        {
            openLoops = loops.Take(10).Select(l => new { description = ToolArgs.Clip(l.Description, 150), l.Owner }),
            anticipations = anticipations.Take(10).Select(a => new
            {
                description = a.Description,
                eventAt = a.EventAt,
                status = a.Status.ToString(),
            }),
            curiosities = curiosities.Take(10).Select(c => new { c.Question, c.About }),
        });
    }
}

/// <summary>procedure.search — find things the user has taught her how to do (read-only).</summary>
public sealed class ProcedureSearchTool : ICompanionTool
{
    private readonly IProcedureStore _procedures;

    public ProcedureSearchTool(IProcedureStore procedures) => _procedures = procedures;

    public string Name => "procedure.search";
    public string Description =>
        "Search the procedures the user has taught you (how they like things done) and read their steps.";
    public string ArgumentsHint => "{\"query\": \"what kind of procedure\"}";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var query = ToolArgs.Text(arguments, "query", 200);
        if (query is null)
            return ToolResult.Fail("invalid_arguments", "Provide a non-empty \"query\" string.");

        var found = await _procedures.SearchAsync(userId, query, limit: 5, ct);
        return ToolResult.Success(new
        {
            query,
            procedures = found.Select(p => new
            {
                p.Name,
                description = ToolArgs.Clip(p.Description, 150),
                steps = p.Steps.Where(s => s.IsActive).OrderBy(s => s.Order)
                    .Take(12).Select(s => ToolArgs.Clip(s.Instruction, 150)),
            }),
        });
    }
}

/// <summary>preference.list — her own tastes, so she can answer "what do you like?" from real state.</summary>
public sealed class PreferenceListTool : ICompanionTool
{
    private readonly IPreferenceStore _preferences;

    public PreferenceListTool(IPreferenceStore preferences) => _preferences = preferences;

    public string Name => "preference.list";
    public string Description =>
        "List your own formed tastes and opinions (yours, not the user's) — use when asked what you " +
        "like or think about things, so you answer from your real, evolved preferences.";
    public string ArgumentsHint => "{} (no arguments)";
    public bool Available => true;

    public async Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var prefs = await _preferences.GetAllAsync(userId, ct);
        return ToolResult.Success(new
        {
            preferences = prefs.Take(15).Select(p => new
            {
                p.Subject,
                feeling = p.Describe(),
                observations = p.Observations,
            }),
        });
    }
}

/// <summary>
/// diagnostics.last_turn — grounded introspection: what actually happened on recent turns (what
/// was retrieved, which context sections existed, how generation went, which tools ran). Answers
/// "why did you say that?" from the operational record, never from confabulation. No secrets: the
/// ring holds only names, counts, and bounded previews of content the model already saw.
/// </summary>
public sealed class DiagnosticsTool : ICompanionTool
{
    private readonly ITurnTraceLog _log;

    public DiagnosticsTool(ITurnTraceLog log) => _log = log;

    public string Name => "diagnostics.last_turn";
    public string Description =>
        "Inspect what actually happened on your recent turns — which memories were used, which " +
        "context you had, and which tools ran. Use when asked why you said or brought up something.";
    public string ArgumentsHint => "{\"turns\": \"1-3 (optional, default 1)\"}";
    public bool Available => true;

    public Task<ToolResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken ct = default)
    {
        var count = ToolArgs.Limit(arguments, "turns", fallback: 1, max: 3);
        var recent = _log.Recent(userId, count);
        if (recent.Count == 0)
            return Task.FromResult(ToolResult.Fail("not_found", "No turn diagnostics recorded yet this session."));

        return Task.FromResult(ToolResult.Success(new
        {
            turns = recent.Select(t => new
            {
                at = t.At,
                userMessage = t.UserMessagePreview,
                memoriesRetrieved = t.MemoriesRetrieved,
                memoriesUsed = t.RetrievedSummaries,
                contextSections = t.ContextSections,
                project = t.DetectedProject,
                inCharacter = t.InCharacterTurn,
                privateConversation = t.PrivateConversation,
                generation = new { t.FinishReason, rounds = t.GenerationRounds, model = t.ModelUsed },
                toolCalls = t.ToolCalls.Select(c => new { c.Tool, c.Ok, c.Code, c.DurationMs }),
                toolDecisions = t.ToolDecisions,
            }),
        }));
    }
}
