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
        Console.WriteLine("Persistent AI Companion (Phase 2 vertical slice).");
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
            Console.WriteLine($"  - ({tag}) {m.Content}{status}  (confidence {m.Confidence:P0})");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  /seed      Load a few months of demo history");
        Console.WriteLine("  /remember  Show what I remember about you");
        Console.WriteLine("  /why       Explain how the last response was retrieved");
        Console.WriteLine("  /help      Show this help");
        Console.WriteLine("  /exit      Quit");
    }
}
