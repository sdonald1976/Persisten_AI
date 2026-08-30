using System.Net;
using System.Text;
using System.Text.Json;
using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Run-2 in the turn path: what it is allowed to change, and what it must never change.
///
/// The invariants are the whole point of a canary. Run-2 may be displayed only when every
/// critical gate passes; anything else — unavailable, slow, empty, malformed, or failing a gate —
/// shows the production reply that was already generated. It is never both, never neither, and a
/// rejected candidate leaves no trace in anything durable.
/// </summary>
public class MouthCanaryTests : IAsyncDisposable
{
    private readonly List<HttpListener> _listeners = [];

    // ---- who may see it -----------------------------------------------------------------------

    [Fact]
    public async Task TheCanaryIsScopedToExactlyOneNamedUser()
    {
        await using var service = Service(canaryUser: "demo-user");

        Assert.True(service.IsMouthCanaryFor("demo-user"));
        Assert.False(service.IsMouthCanaryFor("scott"));
        Assert.False(service.IsMouthCanaryFor("demo-user2"));
        Assert.False(service.IsMouthCanaryFor(""));
    }

    [Fact]
    public async Task NamingNoUserMeansShadowOnlyForEveryone()
    {
        await using var service = Service(canaryUser: "");

        Assert.True(service.IsMouthObserving);
        Assert.False(service.IsMouthCanaryFor("demo-user"));
        Assert.False(service.IsMouthCanaryFor("scott"));
    }

    [Fact]
    public async Task DisabledMeansNeitherShadowNorCanary()
    {
        await using var service = Service(enabled: false, canaryUser: "demo-user");

        Assert.False(service.IsMouthObserving);
        Assert.False(service.IsMouthCanaryFor("demo-user"));
    }

    // ---- what falls back ----------------------------------------------------------------------

    [Fact]
    public async Task ACleanReplyIsOfferedForDisplay()
    {
        await using var service = Service(reply: "Second build came through fine.");

        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.NotNull(result);
        Assert.False(result!.CriticalFailure);
        Assert.Equal("Second build came through fine.", result.Reply);
    }

    [Fact]
    public async Task AnEmptyReplyIsACriticalFailure()
    {
        await using var service = Service(reply: "");

        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.NotNull(result);
        Assert.True(result!.CriticalFailure);
    }

    [Fact]
    public async Task SpokenControlMachineryIsACriticalFailure()
    {
        await using var service = Service(reply: "must_express: the build finished. So: it finished.");

        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.NotNull(result);
        Assert.True(result!.CriticalFailure);
    }

    [Fact]
    public async Task AnUnreachableEndpointFallsBackWithoutThrowing()
    {
        // No listener on this port at all.
        await using var service = Service(port: FreePort(), startListener: false);

        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.Null(result);          // null means "show production"
        Assert.Equal(1, service.MouthCanaryFallback);
        Assert.Equal(0, service.MouthCanaryDisplayed);
    }

    [Fact]
    public async Task MalformedOutputFallsBackRatherThanThrowing()
    {
        await using var service = Service(rawBody: "{\"not-a-message\": true}");

        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.Null(result);
        Assert.Equal(1, service.MouthCanaryFallback);
    }

    [Fact]
    public async Task AServerErrorFallsBack()
    {
        await using var service = Service(statusCode: 500, rawBody: "boom");

        Assert.Null(await service.RenderMouthForDisplayAsync(Obs(), record: false, default));
        Assert.Equal(1, service.MouthCanaryFallback);
    }

    [Fact]
    public async Task ATimeoutFallsBackInsteadOfMakingTheUserWait()
    {
        await using var service = Service(delay: TimeSpan.FromSeconds(30), canaryTimeoutSeconds: 5);

        var started = DateTimeOffset.UtcNow;
        var result = await service.RenderMouthForDisplayAsync(Obs(), record: false, default);
        var waited = DateTimeOffset.UtcNow - started;

        Assert.Null(result);
        Assert.True(waited < TimeSpan.FromSeconds(20), $"waited {waited.TotalSeconds:0}s");
    }

    // ---- what it must never do ------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheNativePlanTheMouthCannotRender()
    {
        // No plan/4, no render: there is nothing to be faithful to, and the turn stays on
        // production rather than being answered from a reconstruction.
        await using var service = Service(reply: "anything");

        var result = await service.RenderMouthForDisplayAsync(
            Obs() with { NativeV3 = null }, record: false, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task WithoutTheContextPacketTheMouthCannotRender()
    {
        // The packet IS the system message run-2 was trained under. Rendering without it would
        // send the model a prompt it has never seen and call the result a comparison.
        await using var service = Service(reply: "anything");

        var result = await service.RenderMouthForDisplayAsync(
            Obs() with { Packet = null }, record: false, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task ShadowObservationNeverReturnsAReplyToDisplay()
    {
        // ObserveMouth has no return value by construction - there is no arrangement in which a
        // shadow render reaches the user.
        await using var service = Service(reply: "shadow reply");

        service.ObserveMouth(Obs());

        Assert.Equal(typeof(void), typeof(RendererShadowService)
            .GetMethod(nameof(RendererShadowService.ObserveMouth))!.ReturnType);
    }

    [Fact]
    public async Task ANotRecordedTurnWritesNothing()
    {
        var recorder = new CountingRecorder();
        await using var service = Service(reply: "fine", recorder: recorder);

        await service.RenderMouthForDisplayAsync(Obs(), record: false, default);

        Assert.Equal(0, recorder.Recorded);
    }

    [Fact]
    public async Task ARecordedMouthRowCarriesTheMouthAdapterNotRun1cs()
    {
        // A row that records run-1c's adapter hash beside run-2's output is not evidence, it is a
        // mislabelled sample - and both arms write through the same recorder.
        const string mouthSha = "a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d";
        var recorder = new CapturingRecorder();
        await using var service = Service(reply: "Build came through fine.", recorder: recorder,
            identitySha: mouthSha, pinnedSha: mouthSha);

        await service.RenderMouthForDisplayAsync(Obs(), record: true, default);

        // The identity rides in the row's envelope, beside the latency and the violations.
        var row = Assert.Single(recorder.Rows);
        using var envelope = JsonDocument.Parse(row.Input);
        var recorded = envelope.RootElement
            .EnumerateObject()
            .First(prop => prop.NameEquals("AdapterSha256") || prop.NameEquals("adapterSha256"))
            .Value.GetString();

        Assert.Equal(mouthSha, recorded);
    }

    // ---- identity -------------------------------------------------------------------------------

    [Fact]
    public async Task AMismatchedAdapterIsRefused()
    {
        // A hash in configuration and a process answering on a port are two different claims.
        await using var service = Service(
            identitySha: "0000000000000000000000000000000000000000000000000000000000000000",
            pinnedSha: "a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d");

        var (ok, detail) = await service.VerifyMouthIdentityAsync(default);

        Assert.False(ok);
        Assert.Contains("mismatch", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMatchingAdapterIsAccepted()
    {
        const string sha = "a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d";
        await using var service = Service(identitySha: sha, pinnedSha: sha);

        var (ok, _) = await service.VerifyMouthIdentityAsync(default);

        Assert.True(ok);
        Assert.Equal(sha, service.MouthLoadedAdapterSha);
    }

    [Fact]
    public async Task AnUnpinnedAdapterIsRefusedEvenWhenTheEndpointAnswers()
    {
        await using var service = Service(identitySha: "abc", pinnedSha: "");

        var (ok, detail) = await service.VerifyMouthIdentityAsync(default);

        Assert.False(ok);
        Assert.Contains("no AdapterSha256 pinned", detail, StringComparison.Ordinal);
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private RendererShadowService Service(
        bool enabled = true,
        string canaryUser = "demo-user",
        string? reply = "ok",
        string? rawBody = null,
        int statusCode = 200,
        TimeSpan? delay = null,
        int canaryTimeoutSeconds = 30,
        int? port = null,
        bool startListener = true,
        string identitySha = "a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d",
        string pinnedSha = "a86caf4ad829fef6a427d39066ac5a744cf563934df080c8190713b52cfa235d",
        IShadowRecorder? recorder = null)
    {
        var chosen = port ?? FreePort();
        if (startListener)
            chosen = StartListener(chosen, reply, rawBody, statusCode, delay, identitySha);

        return new RendererShadowService(
            recorder ?? new CountingRecorder(),
            Options.Create(new CompanionOptions
            {
                RendererShadow = new RendererShadowOptions
                {
                    Enabled = true,
                    Mouth = new MouthOptions
                    {
                        Enabled = enabled,
                        Endpoint = $"http://127.0.0.1:{chosen}",
                        CanaryUserId = canaryUser,
                        CanaryTimeoutSeconds = canaryTimeoutSeconds,
                        TimeoutSeconds = 30,
                        AdapterSha256 = pinnedSha,
                    },
                },
            }),
            NullLogger<RendererShadowService>.Instance,
            drainWindow: TimeSpan.FromMilliseconds(50));
    }

    /// <summary>
    /// Bind, retrying on conflict. Probing for a free port and then binding it is a race - the
    /// probe releases the port before the listener claims it - and under xunit's parallel
    /// execution the loser silently steals a port another suite is already serving on.
    /// </summary>
    private int StartListener(
        int port, string? reply, string? rawBody, int statusCode, TimeSpan? delay, string identitySha)
    {
        HttpListener listener;
        while (true)
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                port = FreePort();
            }
        }
        _listeners.Add(listener);

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                string body;
                if (ctx.Request.Url!.AbsolutePath.Contains("identity", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new { adapterSha256 = identitySha });
                }
                else if (ctx.Request.Url.AbsolutePath.Contains("/api/ps", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new { models = new[] { new { size_vram = 6_000_000_000L } } });
                }
                else
                {
                    if (delay is { } d)
                        await Task.Delay(d);
                    body = rawBody ?? JsonSerializer.Serialize(
                        new { message = new { role = "assistant", content = reply ?? "" } });
                    ctx.Response.StatusCode = statusCode;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    // The client gave up first; that is the case under test.
                }
            }
        });
        return port;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static RendererShadowObservation Obs() => new()
    {
        TraceId = Guid.NewGuid(),
        UserId = "demo-user",
        Plan = new ResponsePlan
        {
            Act = TurnIntent.Acknowledge,
            Content = [],
            Epistemic = [],
            Tone = new ToneGuidance("short and casual", null, null),
        },
        Packet = TestPacket(),
        Transcript = [("user", "how'd it go?")],
        UserMessage = "did it work in the end?",
        ProductionResponse = "It worked.",
        NativeV3 = TestPlanV4(),
    };

    private static ContextPacket TestPacket() => new()
    {
        UserMessage = "did it work in the end?",
        Persona = "Ava is a persistent companion talking with Scott.",
    };

    private static Companion.PlanV3.PlanV3 TestPlanV4() => new()
    {
        TraceId = Guid.NewGuid(),
        Participants =
        [
            new Companion.PlanV3.Participant(
                "usr-scott", Companion.PlanV3.ParticipantRole.user, "Scott"),
            new Companion.PlanV3.Participant(
                "cmp-ava", Companion.PlanV3.ParticipantRole.companion, "Ava"),
        ],
        Act = "answer-question",
        Question = new Companion.PlanV3.QuestionPolicyBlock(
            Companion.PlanV3.QuestionPolicy.question_forbidden),
        Items = [],
        Register = Companion.PlanV3.PlanV3Codec.Canonicalize(
            new Companion.PlanV3.RegisterVector()),
    };

    private sealed class CapturingRecorder : CountingRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];

        public override Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
        {
            Rows.Add(comparison);
            return base.RecordAsync(comparison, ct);
        }
    }

    private class CountingRecorder : IShadowRecorder
    {
        public int Recorded;

        public bool IsRecording => true;

        public bool IsShadowing => true;

        public virtual Task RecordAsync(ShadowComparison comparison, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Recorded);
            return Task.CompletedTask;
        }

        public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ForgetByEvidenceAsync(
            string userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now,
            Guid? memoryId = null, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(
            DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);

        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);

        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(
            string? subject, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var l in _listeners)
        {
            try
            {
                l.Stop();
                l.Close();
            }
            catch (Exception)
            {
                // Test teardown.
            }
        }
        await Task.CompletedTask;
    }
}
