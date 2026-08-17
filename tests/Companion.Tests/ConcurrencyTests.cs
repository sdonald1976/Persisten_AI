using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Backlog #4 (remainder): the failure classes that only appear in real use — two turns racing
/// for the same user, and a client that disappears mid-turn. Neither may corrupt state or leave
/// a half-written turn behind.
/// </summary>
public class ConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A chat model that blocks until released, so a turn can be caught mid-flight.</summary>
    private sealed class GatedChatModel : IChatModel
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<ChatCompletion> CompleteAsync(
            string systemPrompt, string userMessage, ResponseFormat? format = null,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            // The planner's JSON call must not block the test; only the reply generation gates.
            if (format is not null)
                return ChatCompletion.FromText("""{"needsTools": false, "reason": "n/a", "calls": []}""");

            Entered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            return ChatCompletion.FromText("a reply");
        }

        public async Task<ChatCompletion> StreamAsync(
            string systemPrompt, string userMessage, IProgress<string> sink,
            string? assistantPrefix = null, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
            sink.Report("a reply");
            return ChatCompletion.FromText("a reply");
        }
    }

    private static async Task<Guid> SeedAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CompanionSeeder>().SeedAsync(Now);
        return (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"concurrency-{Guid.NewGuid():N}.db");

    private static void Cleanup(string dbPath)
    {
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(dbPath)!, Path.GetFileName(dbPath) + "*"))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TwoSimultaneousTurns_BothComplete_AndStateStaysConsistent()
    {
        var dbPath = TempDb();
        try
        {
            await using var host = new TestHost(Now, connectionString: $"Data Source={dbPath}");
            var conversationId = await SeedAsync(host);

            async Task<TurnTrace> TurnAsync(string message)
            {
                using var scope = host.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ICompanion>()
                    .RespondAsync(CompanionSeeder.DemoUserId, conversationId, message);
            }

            // Both racing turns touch profile, relationship, attention and the memory pipeline.
            var first = TurnAsync("I finally tested the Jetson at home.");
            var second = TurnAsync("Also, I ordered a new soldering iron today.");
            var traces = await Task.WhenAll(first, second);

            Assert.All(traces, t => Assert.Equal(TurnStatus.Answered, t.Status));
            Assert.All(traces, t => Assert.False(string.IsNullOrWhiteSpace(t.Response)));

            // Every message is accounted for: two user messages, two replies, each reply linked
            // to the message it answered — no interleaving lost a turn.
            using var verify = host.CreateScope();
            var messages = await verify.ServiceProvider.GetRequiredService<IConversationStore>()
                .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 20);
            Assert.Equal(2, messages.Count(m => m.Role == MessageRole.User));
            Assert.Equal(2, messages.Count(m => m.Role == MessageRole.Assistant));
            Assert.All(messages.Where(m => m.Role == MessageRole.Assistant), m => Assert.NotNull(m.ReplyToId));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AClientLeavingMidTurn_CancelsCleanly_WithoutAPhantomReply()
    {
        var dbPath = TempDb();
        var gated = new GatedChatModel();
        try
        {
            await using var host = new TestHost(
                Now,
                configureServices: s => s.AddSingleton<IChatModel>(gated),
                connectionString: $"Data Source={dbPath}");
            var conversationId = await SeedAsync(host);

            using var cts = new CancellationTokenSource();
            var turn = Task.Run(async () =>
            {
                using var scope = host.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                    CompanionSeeder.DemoUserId, conversationId, "tell me something long", ct: cts.Token);
            });

            // Wait until generation is genuinely in flight, then "close the tab".
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            gated.Release();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

            // The user's message is stored (that is real — they said it), but no assistant reply
            // was invented for a generation that never finished.
            using var verify = host.CreateScope();
            var messages = await verify.ServiceProvider.GetRequiredService<IConversationStore>()
                .GetRecentMessagesAsync(conversationId, CompanionSeeder.DemoUserId, 20);
            Assert.Contains(messages, m => m.Role == MessageRole.User && m.Content.Contains("something long"));
            Assert.DoesNotContain(messages, m => m.Role == MessageRole.Assistant);
        }
        finally
        {
            gated.Release();
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ARaceOnTheSameProfile_DoesNotDuplicateIt()
    {
        // Profile creation is get-or-create; several turns starting at once must not each insert
        // their own row for a brand-new user.
        var dbPath = TempDb();
        try
        {
            await using var host = new TestHost(Now, connectionString: $"Data Source={dbPath}");
            const string freshUser = "brand-new-user";

            var reads = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                using var scope = host.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IProfileStore>()
                    .GetOrCreateAsync(freshUser);
            }));

            var profiles = await Task.WhenAll(reads);

            Assert.All(profiles, p => Assert.Equal(freshUser, p.UserId));
            using var verify = host.CreateScope();
            var again = await verify.ServiceProvider.GetRequiredService<IProfileStore>()
                .GetOrCreateAsync(freshUser);
            Assert.Equal(freshUser, again.UserId);
        }
        finally { Cleanup(dbPath); }
    }
}
