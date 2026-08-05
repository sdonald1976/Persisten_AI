using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace Companion.Cli;

/// <summary>Interactive REPL for the companion, plus the `/why` diagnostics and memory commands.</summary>
public sealed class ChatLoop
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly string _userId;

    public ChatLoop(IServiceProvider services, TimeProvider clock, string userId)
    {
        _services = services;
        _clock = clock;
        _userId = userId;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.WriteLine("Persistent AI Companion.");
        Console.WriteLine("Type a message, or /help for commands. /exit to quit.");
        Console.WriteLine();

        // Each REPL session is one conversation.
        Guid conversationId;
        using (var scope = _services.CreateScope())
        {
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            var conv = await conversations.StartConversationAsync(
                _userId, title: "CLI session", modelUsed: "mock", source: "cli", ct);
            conversationId = conv.Id;
        }

        TurnTrace? lastTrace = null;

        while (!ct.IsCancellationRequested)
        {
            Console.Write("you> ");
            var input = Console.ReadLine();
            if (input is null)
                break;
            input = input.Trim();
            if (input.Length == 0)
                continue;

            if (input.StartsWith('/'))
            {
                if (await HandleCommandAsync(input, lastTrace, ct))
                    break; // /exit
                continue;
            }

            using var scope = _services.CreateScope();
            var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();
            lastTrace = await companion.RespondAsync(_userId, conversationId, input, ct);
            Console.WriteLine();
            Console.WriteLine($"companion> {lastTrace.Response}");
            Console.WriteLine("(type /why to see how that was retrieved)");
            Console.WriteLine();
        }
    }

    /// <summary>Returns true if the loop should exit.</summary>
    private async Task<bool> HandleCommandAsync(string input, TurnTrace? lastTrace, CancellationToken ct)
    {
        var command = input.Split(' ', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/exit":
            case "/quit":
                return true;

            case "/help":
                PrintHelp();
                return false;

            case "/why":
                if (lastTrace is null)
                    Console.WriteLine("No turn yet — say something first.");
                else
                    Console.WriteLine(TraceRenderer.Render(lastTrace));
                return false;

            case "/seed":
                await SeedAsync(ct);
                return false;

            case "/remember":
                await ShowMemoriesAsync(ct);
                return false;

            case "/projects":
                await ShowProjectsAsync(ct);
                return false;

            case "/project":
                await ShowProjectAsync(input.Length > "/project".Length ? input["/project".Length..].Trim() : "", ct);
                return false;

            case "/loops":
                await ShowLoopsAsync(ct);
                return false;

            case "/forget":
                await ForgetAsync(Arg(input), ct);
                return false;

            case "/dispute":
                await DisputeAsync(Arg(input), ct);
                return false;

            case "/correct":
                await CorrectAsync(Arg(input), ct);
                return false;

            case "/reassign":
                await ReassignAsync(Arg(input), ct);
                return false;

            case "/mergeprojects":
                await MergeProjectsAsync(Arg(input), ct);
                return false;

            case "/consolidate":
                await ConsolidateAsync(ct);
                return false;

            default:
                Console.WriteLine($"Unknown command '{command}'. Try /help.");
                return false;
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<CompanionSeeder>();
        var seeded = await seeder.SeedAsync(_clock.GetUtcNow(), ct);
        Console.WriteLine(seeded
            ? $"Seeded demo history for '{CompanionSeeder.DemoUserId}'."
            : "Already seeded (skipped).");
    }

    /// <summary>Phase-2 stand-in for "what do you remember about me?" (full controls arrive in Phase 5).</summary>
    private async Task ShowMemoriesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var memories = await store.GetRetrievableMemoriesAsync(_userId, ct);
        if (memories.Count == 0)
        {
            Console.WriteLine("I don't have any memories for you yet. Try /seed for demo data.");
            return;
        }

        Console.WriteLine($"I currently remember {memories.Count} things about you:");
        foreach (var m in memories.OrderByDescending(m => m.EffectiveAt))
        {
            var tag = m.Kind == MemoryKind.Semantic ? "fact " : "event";
            var status = m is SemanticMemory { Validity: not Validity.Current } sm
                ? $" [{sm.Validity}]"
                : "";
            Console.WriteLine($"  - [{m.Id.ToString()[..8]}] ({tag}) {m.Content}{status}  (confidence {m.Confidence:P0})");
        }
        Console.WriteLine("(use the [id] with /forget, /correct, /dispute, /reassign)");
    }

    private static string Arg(string input)
    {
        var space = input.IndexOf(' ');
        return space < 0 ? "" : input[(space + 1)..].Trim();
    }

    /// <summary>Finds a retrievable memory by an id prefix (as shown by /remember).</summary>
    private async Task<Guid?> ResolveMemoryIdAsync(string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;
        using var scope = _services.CreateScope();
        var memories = await scope.ServiceProvider.GetRequiredService<IMemoryStore>()
            .GetRetrievableMemoriesAsync(_userId, ct);
        var matches = memories
            .Where(m => m.Id.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1)
            return matches[0].Id;
        Console.WriteLine(matches.Count == 0 ? $"No memory matches id '{prefix}'." : $"'{prefix}' is ambiguous.");
        return null;
    }

    private async Task ForgetAsync(string prefix, CancellationToken ct)
    {
        var id = await ResolveMemoryIdAsync(prefix, ct);
        if (id is null) return;
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMemoryCurator>().ForgetAsync(_userId, id.Value, "user asked to forget", ct);
        Console.WriteLine("Forgotten. I won't bring that up again.");
    }

    private async Task DisputeAsync(string prefix, CancellationToken ct)
    {
        var id = await ResolveMemoryIdAsync(prefix, ct);
        if (id is null) return;
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMemoryCurator>().DisputeAsync(_userId, id.Value, "user disputed", ct);
        Console.WriteLine("Noted — I've flagged that as disputed and won't rely on it.");
    }

    private async Task CorrectAsync(string args, CancellationToken ct)
    {
        var parts = args.Split(' ', 2);
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /correct <id> <the corrected fact>");
            return;
        }
        var id = await ResolveMemoryIdAsync(parts[0], ct);
        if (id is null) return;
        using var scope = _services.CreateScope();
        var ok = await scope.ServiceProvider.GetRequiredService<IMemoryCurator>()
            .CorrectSemanticAsync(_userId, id.Value, parts[1].Trim(), parts[1].Trim(), ct);
        Console.WriteLine(ok ? "Corrected." : "That id isn't a correctable (semantic) memory.");
    }

    private async Task ReassignAsync(string args, CancellationToken ct)
    {
        var parts = args.Split(' ', 2);
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /reassign <id> <project name>");
            return;
        }
        var id = await ResolveMemoryIdAsync(parts[0], ct);
        if (id is null) return;
        using var scope = _services.CreateScope();
        var ok = await scope.ServiceProvider.GetRequiredService<IMemoryCurator>()
            .ReassignMemoryProjectAsync(_userId, id.Value, parts[1].Trim(), ct);
        Console.WriteLine(ok ? $"Re-associated with '{parts[1].Trim()}'." : "No such memory.");
    }

    private async Task MergeProjectsAsync(string args, CancellationToken ct)
    {
        var parts = args.Split(" into ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: /mergeprojects <source> into <target>");
            return;
        }
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var resolver = sp.GetRequiredService<IEntityResolver>();
        var source = (await resolver.ResolveProjectAsync(_userId, parts[0], ct)).Best?.Project;
        var target = (await resolver.ResolveProjectAsync(_userId, parts[1], ct)).Best?.Project;
        if (source is null || target is null)
        {
            Console.WriteLine("Couldn't confidently resolve both projects by name.");
            return;
        }
        var ok = await sp.GetRequiredService<IProjectCurator>().MergeProjectsAsync(_userId, source.Id, target.Id, ct);
        Console.WriteLine(ok ? $"Merged '{source.Name}' into '{target.Name}'." : "Merge failed.");
    }

    private async Task ShowProjectsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var projects = await scope.ServiceProvider.GetRequiredService<IProjectStore>().GetProjectsAsync(_userId, ct);
        if (projects.Count == 0)
        {
            Console.WriteLine("No projects yet. Try /seed for demo data.");
            return;
        }
        Console.WriteLine($"Projects ({projects.Count}):");
        foreach (var p in projects.OrderByDescending(p => p.LastActivityAt))
            Console.WriteLine($"  - {p.Name}  [{p.Status}]");
    }

    private async Task ShowProjectAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Usage: /project <name or reference>");
            return;
        }

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var resolution = await sp.GetRequiredService<IEntityResolver>().ResolveProjectAsync(_userId, name, ct);

        if (resolution.RequiresClarification)
        {
            Console.WriteLine(resolution.ClarificationQuestion);
            return;
        }
        if (resolution.Best is null)
        {
            Console.WriteLine($"I don't have a project matching \"{name}\".");
            return;
        }

        var summary = await sp.GetRequiredService<IProjectContextService>()
            .GetSummaryAsync(_userId, resolution.Best.Project.Id, ct);
        if (summary is null)
            return;

        Console.WriteLine($"# {summary.Project.Name}  [{summary.Project.Status}]");
        if (!string.IsNullOrWhiteSpace(summary.Project.Purpose))
            Console.WriteLine(summary.Project.Purpose);
        if (summary.Decisions.Count > 0)
        {
            Console.WriteLine("\nDecisions:");
            foreach (var d in summary.Decisions)
                Console.WriteLine($"  - {d.Statement}");
        }
        if (summary.OpenLoops.Count > 0)
        {
            Console.WriteLine("\nOpen loops:");
            foreach (var l in summary.OpenLoops)
                Console.WriteLine($"  - {l.Description}");
        }
        if (summary.RecentEvents.Count > 0)
        {
            Console.WriteLine("\nRecent activity:");
            foreach (var e in summary.RecentEvents)
                Console.WriteLine($"  - {e.Description}");
        }
    }

    private async Task ShowLoopsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var loops = await scope.ServiceProvider.GetRequiredService<IProjectStore>()
            .GetOpenLoopsAsync(_userId, onlyOpen: true, ct);
        if (loops.Count == 0)
        {
            Console.WriteLine("No open loops.");
            return;
        }
        Console.WriteLine($"Open loops ({loops.Count}):");
        foreach (var l in loops)
            Console.WriteLine($"  - {l.Description}");
    }

    private async Task ConsolidateAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMemoryConsolidator>().ConsolidateAsync(_userId, ct);
        if (result.Created.Count == 0)
        {
            Console.WriteLine("Nothing to consolidate yet.");
            return;
        }
        Console.WriteLine($"Consolidated {result.Created.Count} group(s):");
        foreach (var g in result.Created)
            Console.WriteLine($"  - {g.Content}  (from {g.SourceMemoryIds.Count} memories, {g.EvidenceCount} evidence)");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  /seed             Load a few months of demo history");
        Console.WriteLine("  /remember         Show what I remember about you");
        Console.WriteLine("  /projects         List your projects");
        Console.WriteLine("  /project <name>   Reconstruct a project's current state");
        Console.WriteLine("  /loops            List open loops");
        Console.WriteLine("  /forget <id>      Forget a memory (soft delete)");
        Console.WriteLine("  /dispute <id>     Flag a memory as wrong");
        Console.WriteLine("  /correct <id> …   Correct a memory's fact");
        Console.WriteLine("  /reassign <id> …  Re-associate a memory with a project");
        Console.WriteLine("  /mergeprojects <a> into <b>   Merge two project references");
        Console.WriteLine("  /consolidate      Roll repeated memories into higher-level knowledge");
        Console.WriteLine("  /why              Explain how the last response was retrieved");
        Console.WriteLine("  /help             Show this help");
        Console.WriteLine("  /exit             Quit");
    }
}
