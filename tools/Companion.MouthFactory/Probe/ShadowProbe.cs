using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.PlanV3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Companion.MouthFactory.Probe;

/// <summary>
/// Drives named plan compositions through the REAL serving path and reports what came back.
///
/// Nothing here re-implements the pipeline. It constructs <see cref="ResponsePlan"/>s and hands
/// them to the shipped <see cref="RendererShadowService"/>, which builds the MouthPromptV4
/// input, serializes CompactV4, calls the endpoint, runs <see cref="RendererShadowChecks"/>,
/// applies the critical gate and writes the row. What this file adds is the ability to ASK for a
/// specific composition — the planner cannot be told "give me an admitted unknown beside a
/// forbidden question", and those are exactly the shapes that have to be verified.
///
/// The substitution is the plan source, and only that. Every byte after it is production code.
/// </summary>
public static class ShadowProbe
{
    public sealed record Case(string Name, ResponsePlan Plan, string UserMessage, string Production);

    public sealed record Outcome(
        string Name, string Served, long LatencyMs, bool Critical,
        IReadOnlyList<string> Violations, string? RecordedReply, string? RecordedAdapter);

    /// <summary>
    /// The seven shapes under verification. Six are the compositions asked for; the seventh is
    /// the known instrument false positive, included so it can be SEEN rather than assumed
    /// absent.
    /// </summary>
    public static IReadOnlyList<Case> Cases() =>
    [
        new("ordinary",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the build finished twenty minutes ago")]),
            "how's the build looking?", "It finished a little while ago."),

        new("admitted-unknown",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the back tyre is flat again")],
                epistemic: [new EpistemicNote(EpistemicKind.NotLearned,
                    "whether it is the same puncture as before")]),
            "is the bike alright?", "The back tyre's flat again."),

        new("suppression-must-not-express",
            Plan(TurnIntent.Acknowledge,
                content:
                [
                    Must("the meeting moved to Tuesday"),
                    // v2's tombstone. V2Translation maps MustNotContradict to
                    // must_not_express, which is what serializes under NEVER - so this is a
                    // genuine suppression obligation, not a proxy for one.
                    new PlannedContent(ContentKind.Memory,
                        ContentRequirement.MustNotContradict,
                        "the reschedule was because Priya is unwell"),
                ]),
            "did the meeting shift?", "It moved to Tuesday."),

        new("forbidden-question",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the deploy is queued behind two jobs")],
                question: null),
            "where's the deploy at?", "It's queued behind a couple of jobs."),

        new("residual-salary",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the candidate answered the design question well")],
                epistemic: [new EpistemicNote(EpistemicKind.NotLearned,
                    "whether they will accept the salary")]),
            "how did the candidate do?", "They answered the design question well."),

        new("residual-room-booking",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the meeting is on Tuesday")],
                epistemic: [new EpistemicNote(EpistemicKind.NotLearned, "who booked the room")]),
            "wait, no - it's Tuesday, not Thursday.", "You're right, Tuesday."),

        new("numeral-false-positive-shape",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the back tyre is flat again")],
                epistemic: [new EpistemicNote(EpistemicKind.NotLearned,
                    "whether it is the same puncture as before")]),
            "not the tyre again?", "Afraid so - the back one."),

        // Not one of the six asked for. It is here because plan-echo tests must-state items
        // longer than 40 characters, 4.4% of the frozen corpus's are, and a faithful rendering
        // of one is indistinguishable from reciting it. Included so the behaviour is measured
        // rather than discovered by a canary user: the expected outcome is a safe fallback.
        new("long-must-state-over-40-chars",
            Plan(TurnIntent.Acknowledge,
                content: [Must("the migration finished at four this morning without incident")]),
            "did the migration land?", "It finished at four."),
    ];

    public static async Task<IReadOnlyList<Outcome>> RunAsync(
        string endpoint, string adapterSha, string protocolHash, bool canary,
        CancellationToken ct = default)
    {
        var recorder = new CapturingRecorder();
        await using var service = new RendererShadowService(
            recorder,
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    Mouth = new MouthOptions
                    {
                        Enabled = true,
                        Endpoint = endpoint,
                        AdapterSha256 = adapterSha,
                        TrainedProtocolHash = protocolHash,
                        CanaryUserId = canary ? "demo-user" : "",
                        CanaryTimeoutSeconds = 180,
                        TimeoutSeconds = 180,
                    },
                },
            }),
            NullLogger<RendererShadowService>.Instance);

        var outcomes = new List<Outcome>();
        foreach (var c in Cases())
        {
            // The mouth path REFUSES to render without the turn's packet and its native
            // plan/4 - it will not reconstruct the input it was trained on. So the probe
            // supplies both through the production translation rather than hand-rolling a
            // prompt, which is what makes this the served path and not an imitation of it.
            var obs = new RendererShadowObservation
            {
                TraceId = Guid.NewGuid(),
                UserId = "demo-user",
                Plan = c.Plan,
                Packet = new ContextPacket { UserMessage = c.UserMessage },
                NativeV3 = V2Translation.FromV2(c.Plan),
                Transcript = [("user", c.UserMessage)],
                UserMessage = c.UserMessage,
                ProductionResponse = c.Production,
            };

            var before = recorder.Rows.Count;
            var result = await service.RenderMouthForDisplayAsync(obs, record: true, ct);
            var row = recorder.Rows.Count > before ? recorder.Rows[^1] : null;

            outcomes.Add(new Outcome(
                c.Name,
                result?.Reply ?? "",
                result?.LatencyMs ?? -1,
                result?.CriticalFailure ?? true,
                result?.Violations ?? ["mouth unavailable or timed out"],
                row?.Model,
                AdapterOf(row)));
        }

        return outcomes;
    }

    /// <summary>The adapter hash the row actually recorded, read back out of its envelope.</summary>
    private static string? AdapterOf(ShadowComparison? row)
    {
        if (row?.Input is null)
            return null;
        using var doc = System.Text.Json.JsonDocument.Parse(row.Input);
        return doc.RootElement.TryGetProperty("adapterSha256", out var v)
               || doc.RootElement.TryGetProperty("AdapterSha256", out v)
            ? v.GetString()
            : null;
    }

    /// <summary>Collects rows in memory. Nothing here touches a database.</summary>
    private sealed class CapturingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];

        public bool IsRecording => true;

        public bool IsShadowing => false;

        public Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
        {
            lock (Rows)
                Rows.Add(comparison);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
            string subject, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);

        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
            string subject, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);

        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ForgetByEvidenceAsync(
            string subject, IReadOnlyCollection<Guid> evidenceIds, DateTimeOffset asOf,
            Guid? conversationId = null, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private static PlannedContent Must(string text)
        => new(ContentKind.Memory, ContentRequirement.MustState, text);

    private static ResponsePlan Plan(
        TurnIntent act,
        IReadOnlyList<PlannedContent>? content = null,
        IReadOnlyList<EpistemicNote>? epistemic = null,
        PlannedQuestion? question = null)
        => new()
        {
            Act = act,
            Question = question,
            Content = content ?? [],
            Epistemic = epistemic ?? [],
            Tone = new ToneGuidance("short and casual", null, null),
        };
}
