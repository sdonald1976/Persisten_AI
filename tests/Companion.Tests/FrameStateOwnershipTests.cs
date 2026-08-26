using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// R-02. Frame truth belongs to cognition, not to observation.
///
/// The frame lifecycle used to run inside the renderer-shadow gate, so turning observation
/// off stopped the frame from advancing and from persisting. An observability flag decided
/// whether durable conversation state moved — which is the inversion of the rule that
/// observability cannot affect the turn.
///
/// The tests below run the SAME conversation with shadow on and shadow off and require the
/// frame history to be identical, while requiring the shadow rows to differ exactly as the
/// flag intends.
/// </summary>
public class FrameStateOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Scene =
    [
        "Let's roleplay: you're a lighthouse keeper and I'm a sailor.",
        "*shakes the rain off my coat* Rough crossing tonight.",
        "switch to the other character",
        "ok, out of character for a sec",
    ];

    private sealed record TurnOutcome(
        IReadOnlyList<string> Transitions,
        IReadOnlyList<string> Replies,
        IReadOnlyList<string> FrameVerdicts,
        int ShadowRows);

    /// <summary>Runs the whole scene through a real host at a given shadow setting.</summary>
    private static async Task<TurnOutcome> RunSceneAsync(
        bool shadowEnabled, string? dbPath = null, IEnumerable<string>? script = null)
    {
        var recorder = new CollectingRecorder();
        var settings = new Dictionary<string, string?>
        {
            ["Companion:RendererShadow:Enabled"] = shadowEnabled ? "true" : "false",
            // A port nothing is listening on: the shadow render fails, which is the point.
            // Enabled-ness is what is under test, not the renderer's availability.
            ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
            ["Companion:RendererShadow:TimeoutSeconds"] = "2",
        };

        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            connectionString: dbPath is null ? null : $"Data Source={dbPath}",
            settings: settings);

        Guid conversationId;
        using (var seed = host.CreateScope())
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;

        var replies = new List<string>();
        foreach (var message in script ?? Scene)
        {
            using var scope = host.CreateScope();
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>()
                .RespondAsync(CompanionSeeder.DemoUserId, conversationId, message);
            replies.Add(trace.Response);
        }

        if (host.Services.GetRequiredService<IRendererShadow>() is RendererShadowService svc)
            await svc.DisposeAsync();

        // Read the durable frame history back out of the database.
        using var read = host.CreateScope();
        var db = read.ServiceProvider
            .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var sessions = await db.FrameSessions.AsNoTracking()
            .Where(s => s.UserId == CompanionSeeder.DemoUserId)
            .OrderBy(s => s.EnteredAt)
            .ToListAsync();

        var transitions = sessions
            .SelectMany(s => JsonSerializer.Deserialize<List<FrameTransitionEntry>>(
                s.TransitionLogJson) ?? [])
            .Select(e => e.Transition)
            .ToList();

        // The ordered decision evidence lives in the diagnostics store, not the trace.
        var verdicts = (await read.ServiceProvider.GetRequiredService<IDiagnosticsStore>()
                .GetRecentTurnsAsync(CompanionSeeder.DemoUserId, 50))
            .OrderBy(t => t.Timestamp)
            .SelectMany(t => (t.Decisions ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries))
            .Where(d => d.StartsWith("plan.frame=", StringComparison.Ordinal))
            .Select(d => d["plan.frame=".Length..])
            .ToList();

        return new TurnOutcome(transitions, replies, verdicts, recorder.Rows.Count);
    }

    // ---- the core equivalence -----------------------------------------------------------------

    [Fact]
    public async Task FrameTransitions_AreIdentical_WithShadowOnAndOff()
    {
        var on = await RunSceneAsync(shadowEnabled: true);
        var off = await RunSceneAsync(shadowEnabled: false);

        // The whole point: durable frame state does not depend on whether anyone is watching.
        Assert.Equal(on.Transitions, off.Transitions);
        Assert.NotEmpty(off.Transitions);
        Assert.Equal("enter", off.Transitions[0]);
    }

    [Fact]
    public async Task TheFrameDecisionIsRecorded_EvenWithShadowDisabled()
    {
        var off = await RunSceneAsync(shadowEnabled: false);

        // Before R-02 this list was empty with shadow off: the stage never ran at all.
        Assert.NotEmpty(off.FrameVerdicts);
        Assert.Contains("enter", off.FrameVerdicts);
    }

    [Fact]
    public async Task EveryTransitionKind_Survives_WithShadowDisabled()
    {
        var off = await RunSceneAsync(shadowEnabled: false);

        Assert.Contains("enter", off.Transitions);
        Assert.Contains("exit", off.Transitions);
        // A continue or a switch lands between them depending on how the lifecycle reads the
        // middle turns; requiring at least one proves the scene advanced rather than
        // entering and immediately leaving.
        Assert.True(off.Transitions.Count >= 3,
            $"expected the scene to advance, saw [{string.Join(", ", off.Transitions)}]");
    }

    [Fact]
    public async Task DisablingShadow_StopsShadowRows_AndNothingElse()
    {
        var on = await RunSceneAsync(shadowEnabled: true);
        var off = await RunSceneAsync(shadowEnabled: false);

        // The flag still does its actual job...
        Assert.Equal(0, off.ShadowRows);
        // ...while the conversation is unchanged.
        Assert.Equal(on.Transitions, off.Transitions);
    }

    [Fact]
    public async Task TheDisplayedReply_IsUnaffectedByTheShadowFlag()
    {
        var on = await RunSceneAsync(shadowEnabled: true);
        var off = await RunSceneAsync(shadowEnabled: false);

        Assert.Equal(on.Replies.Count, off.Replies.Count);
        Assert.All(on.Replies, r => Assert.False(string.IsNullOrWhiteSpace(r)));
        // The canary is disabled and the shadow renderer is unreachable in both runs, so
        // production rendered every reply either way.
        Assert.Equal(off.Replies.Count, off.Replies.Count(r => !string.IsNullOrWhiteSpace(r)));
    }

    // ---- privacy is preserved through the move ------------------------------------------------

    [Fact]
    public async Task ASensitiveFrameTurn_StillAdvancesAndStillRecordsNoEvidence()
    {
        // R-01 and R-02 together: the frame moves because the lifecycle is unconditional,
        // and it records no link because the turn was sensitive.
        var dbPath = Path.Combine(Path.GetTempPath(), $"frame-sensitive-{Guid.NewGuid():N}.db");
        try
        {
            var run = await RunSceneAsync(
                shadowEnabled: false,
                dbPath: dbPath,
                // One turn, and a sensitive one: it must still move the frame.
                script: ["Keep this private: let's roleplay, you're a lighthouse keeper."]);

            Assert.Contains("enter", run.Transitions);

            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "select TransitionLogJson from FrameSessions";
            var logs = new List<string>();
            using (var r = cmd.ExecuteReader())
                while (r.Read()) logs.Add(r.GetString(0));

            foreach (var log in logs)
            {
                Assert.DoesNotContain("\"Evidence\"", log, StringComparison.Ordinal);
                foreach (var entry in JsonSerializer.Deserialize<List<FrameTransitionEntry>>(log)!)
                    Assert.Null(entry.EvidenceMessageId);
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    // ---- restart / resume ----------------------------------------------------------------------

    [Fact]
    public async Task AFrameEnteredWithShadowOff_ResumesAfterRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"frame-resume-{Guid.NewGuid():N}.db");
        try
        {
            var first = await RunSceneAsync(
                shadowEnabled: false, dbPath: dbPath,
                script: ["Let's roleplay: you're a lighthouse keeper and I'm a sailor."]);
            Assert.Contains("enter", first.Transitions);

            // A second host over the same file — a restart in every sense that matters.
            await using var host = new TestHost(
                Now, connectionString: $"Data Source={dbPath}",
                settings: new Dictionary<string, string?>
                {
                    ["Companion:RendererShadow:Enabled"] = "false",
                });

            var frames = host.Services.GetRequiredService<IFrameSessionStore>();
            using var scope = host.CreateScope();
            var db = scope.ServiceProvider
                .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
            var session = await db.FrameSessions.AsNoTracking()
                .FirstAsync(s => s.UserId == CompanionSeeder.DemoUserId);

            Assert.Equal(FrameSessionStatus.Active, session.Status);
            Assert.NotNull(await frames.GetActiveAsync(
                CompanionSeeder.DemoUserId, session.ConversationId));
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    // ---- the structural guarantee ---------------------------------------------------------------

    [Fact]
    public void TheFrameLifecycle_IsNotNestedInsideTheShadowGate()
    {
        // Source-level, because this is exactly the coupling that gets reintroduced by
        // someone tidying nearby code. The behavioural tests above prove the current build
        // is correct; this one says WHY it broke if it ever breaks again.
        var lines = File.ReadAllLines(Path.Combine(
            RepoRoot(), "src", "Companion.Core", "Services", "Companion.cs"));

        var frameBlock = Array.FindIndex(lines,
            l => l.StartsWith("        if (_frames is not null)", StringComparison.Ordinal));
        var frameWrite = Array.FindIndex(lines,
            l => l.Contains("_frames.ApplyAsync", StringComparison.Ordinal));

        // Method-level indentation is eight spaces in this file; anything nested inside the
        // renderer-shadow gate sits at sixteen or deeper.
        Assert.True(frameBlock >= 0,
            "the frame lifecycle block is no longer at method level — it may have been "
            + "nested back inside a conditional; frame truth is cognition, not observation");
        Assert.True(frameWrite > frameBlock, "the frame write moved out of its lifecycle block");

        // And nothing between the block's start and its write consults the shadow.
        var between = string.Join(" ", lines[frameBlock..frameWrite]);
        Assert.DoesNotContain("_rendererShadow", between, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];
        public bool IsRecording => true;
        public bool IsShadowing => true;
        public Task RecordAsync(ShadowComparison c, CancellationToken ct = default)
        {
            // Only renderer rows are relevant here; other subsystems record for their own
            // reasons and would drown the signal.
            if (c.Subject is RendererShadowService.RendererV3Subject
                or RendererShadowService.RendererShadowSubject)
                Rows.Add(c);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(DateTimeOffset s, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);
        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows);
        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);
        public Task<int> PruneAsync(DateTimeOffset o, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetCapturesAsync(IReadOnlyCollection<string> e, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
