using Companion.Core;
using Companion.Core.Abstractions;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The specialist-model runtime, and the promise that makes it safe to add: a companion with no
/// models present behaves exactly as it did before they existed.
///
/// This matters more than any individual model. Every heuristic a specialist model would improve on
/// is still in the codebase and still works, so the failure mode of a missing, misconfigured or
/// broken model must be "the old behaviour, and a line in the log" — never a dead companion and
/// never an invented score. A judgment nobody made is not the same as a judgment of zero.
/// </summary>
public class CognitiveModelRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TestHost Build(params (string Key, string Value)[] settings)
        => new(Now, settings: settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));

    [Fact]
    public async Task WithNothingConfigured_EveryModelIsAbsentAndSaysSo()
    {
        await using var host = Build();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        foreach (ICognitiveModel model in new ICognitiveModel[]
                 {
                     sp.GetRequiredService<ITextPairScorer>(),
                     sp.GetRequiredService<INliModel>(),
                     sp.GetRequiredService<ITextClassifier>(),
                 })
        {
            Assert.False(model.IsAvailable);
            Assert.Equal("disabled", model.Status.Reason);
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
        }
    }

    /// <summary>
    /// An enabled model whose file isn't there must not stop the companion starting. This is the
    /// realistic case — configuration moves between machines faster than 90 MB of weights do.
    /// </summary>
    [Fact]
    public async Task AnEnabledModelWithNoFile_FallsBackInsteadOfFailing()
    {
        await using var host = Build(
            ("CognitiveModels:Reranker:Enabled", "true"),
            ("CognitiveModels:Reranker:Path", "definitely-not-here.onnx"));
        using var scope = host.CreateScope();

        var scorer = scope.ServiceProvider.GetRequiredService<ITextPairScorer>();

        Assert.False(scorer.IsAvailable);
        Assert.Contains("not found", scorer.Status.Reason);
        // And it answers rather than throwing, so callers need one branch, not two.
        Assert.Empty(await scorer.ScoreAsync("query", new[] { "a", "b" }));
    }

    /// <summary>
    /// The distinction the whole runtime rests on. An unavailable scorer returns NO scores, not a
    /// list of zeroes — zeroes are a judgment that every candidate is equally irrelevant, and a
    /// caller acting on them would silently discard the heuristic's ranking.
    /// </summary>
    [Fact]
    public async Task NoJudgmentIsNotTheSameAsAJudgmentOfZero()
    {
        await using var host = Build();
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;

        var scores = await sp.GetRequiredService<ITextPairScorer>()
            .ScoreAsync("q", new[] { "a", "b", "c" });
        Assert.Empty(scores);

        Assert.Empty(await sp.GetRequiredService<ITextClassifier>().ClassifyAsync("anything"));

        var verdict = await sp.GetRequiredService<INliModel>().ClassifyAsync("premise", "hypothesis");
        Assert.Equal(0.0, verdict.Confidence);
        Assert.Equal(EntailmentVerdict.Unknown, verdict);
    }

    /// <summary>Models are loaded once and shared; a per-request graph would be paid for every turn.</summary>
    [Fact]
    public async Task ModelsAreSharedNotRebuiltPerScope()
    {
        await using var host = Build();
        using var first = host.CreateScope();
        using var second = host.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ITextPairScorer>(),
            second.ServiceProvider.GetRequiredService<ITextPairScorer>());
    }

    /// <summary>
    /// Required is the escape hatch for someone who would rather know at startup than discover the
    /// old behaviour quietly returned. It has to actually fail, or it means nothing.
    /// </summary>
    [Fact]
    public async Task ARequiredModelThatIsMissing_FailsLoudly()
    {
        await using var host = Build(
            ("CognitiveModels:Reranker:Enabled", "true"),
            ("CognitiveModels:Reranker:Required", "true"),
            ("CognitiveModels:Reranker:Path", "definitely-not-here.onnx"));
        using var scope = host.CreateScope();
        Assert.Throws<FileNotFoundException>(
            () => scope.ServiceProvider.GetRequiredService<ITextPairScorer>());
    }

    [Fact]
    public void DefaultsAreOffAndConservative()
    {
        var options = new CognitiveModelOptions();

        Assert.All(options.All(), m => Assert.False(m.Entry.Enabled));
        Assert.All(options.All(), m => Assert.False(m.Entry.Required));
        Assert.False(options.ShadowMode);
        Assert.True(options.TimeoutMilliseconds > 0);
    }
}
