using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Shadow mode: running a specialist model beside the heuristic it might replace, recording both,
/// and changing nothing.
///
/// The last clause is the whole contract. A shadow comparison that quietly alters a decision is not
/// a shadow — it is an undocumented rollout, and the record it leaves behind is then evidence for a
/// change that has already happened.
/// </summary>
public class ShadowModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TestHost Host(bool shadow)
        => new(Now, settings: new Dictionary<string, string?>
        {
            ["CognitiveModels:ShadowMode"] = shadow ? "true" : "false",
        });

    [Fact]
    public async Task OffByDefault_AndSaysSoSoCallersSkipTheWork()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();

        Assert.False(scope.ServiceProvider.GetRequiredService<IShadowRecorder>().IsRecording);
    }

    /// <summary>
    /// When it is off, the model is not merely ignored — it is never run. Shadowing costs a real
    /// inference per turn, and paying for an answer nobody reads is how a measurement feature turns
    /// into a latency regression.
    /// </summary>
    [Fact]
    public async Task WhenOff_TheModelIsNotEvenCalled()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var called = false;
        var result = await Shadow.CompareAsync<bool>(
            recorder, "test.subject", legacy: true,
            model: _ => { called = true; return Task.FromResult<(bool, double)?>((false, 0.99)); });

        Assert.True(result);
        Assert.False(called);
    }

    /// <summary>The production answer stays the heuristic's, however confident the model is.</summary>
    [Fact]
    public async Task WhenOn_TheLegacyAnswerStillWins()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var result = await Shadow.CompareAsync<bool>(
            recorder, "decision", legacy: false,
            model: _ => Task.FromResult<(bool, double)?>((true, 0.99)),
            input: "I'm actually going with the smaller Jetson after all.");

        Assert.False(result);

        var disagreements = await recorder.GetDisagreementsAsync("decision", 10);
        var recorded = Assert.Single(disagreements);
        Assert.Equal("false", recorded.Legacy);
        Assert.Equal("true", recorded.Model);
        Assert.Equal(0.99, recorded.Confidence);
        Assert.False(recorded.Agreed);
        // Recorded as not applied, explicitly, rather than left to be inferred from a config flag
        // whose value at the time is not in the row.
        Assert.Equal("legacy", recorded.Applied);
    }

    [Fact]
    public async Task AgreementIsAggregatedPerSubject()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        foreach (var (legacy, model) in new[] { (true, true), (true, true), (true, false) })
        {
            await Shadow.CompareAsync(
                recorder, "commitment", legacy,
                _ => Task.FromResult<(bool, double)?>((model, 0.8)));
        }

        var agreement = Assert.Single(await recorder.GetAgreementAsync(Now.AddDays(-1)));
        Assert.Equal("commitment", agreement.Subject);
        Assert.Equal(3, agreement.Comparisons);
        Assert.Equal(1, agreement.Disagreements);
        Assert.Equal(2.0 / 3.0, agreement.AgreementRate, 3);
    }

    /// <summary>
    /// A model that throws in shadow has told us something worth knowing and must not cost a turn.
    /// </summary>
    [Fact]
    public async Task AModelThatThrows_DoesNotBreakTheTurn()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        var result = await Shadow.CompareAsync<bool>(
            recorder, "nli", legacy: true,
            model: _ => throw new InvalidOperationException("model exploded"));

        Assert.True(result);
        Assert.Empty(await recorder.GetDisagreementsAsync("nli", 10));
    }

    /// <summary>Cancelling the turn cancels the shadow work rather than being swallowed as a model failure.</summary>
    [Fact]
    public async Task TurnCancellation_Propagates()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Shadow.CompareAsync<bool>(
            recorder, "nli", legacy: true,
            model: ct => throw new OperationCanceledException(ct),
            ct: cts.Token));
    }

    /// <summary>
    /// The text is only there when someone asked for it. The rest of the telemetry deliberately
    /// holds sizes and outcomes rather than content so it can be kept for weeks without becoming a
    /// second conversation store, and shadow rows live in the same table under the same rule.
    /// </summary>
    [Fact]
    public async Task InputTextIsOnlyStoredWhenSupplied()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        await Shadow.CompareAsync(
            recorder, "quiet", legacy: true,
            model: _ => Task.FromResult<(bool, double)?>((false, 0.7)));

        Assert.Null(Assert.Single(await recorder.GetDisagreementsAsync("quiet", 10)).Input);
    }

    /// <summary>A recorder that cannot write must not surface the failure into the turn.</summary>
    [Fact]
    public async Task RecordingFailure_IsSwallowed()
    {
        await using var host = Host(shadow: true);
        using var scope = host.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IShadowRecorder>();

        // Subject far longer than the column allows: the write fails, the caller does not.
        await recorder.RecordAsync(new ShadowComparison
        {
            Subject = new string('x', 5000),
            Applied = "legacy",
        });
    }
}
