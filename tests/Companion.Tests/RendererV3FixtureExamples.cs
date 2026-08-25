using System.Text.Json;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Generates (and pins) the committed P3 fixture examples in docs/examples/: one
/// non-redacted and one redacted translated_v2 shadow envelope, built entirely from
/// SYNTHETIC plans — no real conversation data. Rerunning the test regenerates the
/// files deterministically; a diff means the envelope contract changed.
/// </summary>
public class RendererV3FixtureExamples
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "RESPONSE_PLAN_V3_SPEC.md")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static ResponsePlan SyntheticPlain() => new()
    {
        TraceId = Guid.Parse("f1f1f1f1-0000-0000-0000-000000000001"),
        Act = TurnIntent.Acknowledge,
        Content =
        [
            new PlannedContent(ContentKind.Interpretation, ContentRequirement.MustState,
                "The synthetic hinge stopped squeaking after one synthetic drop of oil.", "working-context"),
            new PlannedContent(ContentKind.Memory, ContentRequirement.MayUse,
                "The synthetic garden gnome collection reached eleven.", "active"),
        ],
        Tone = new ToneGuidance("short and casual", "good spirits", "warm, dry"),
    };

    [Fact]
    public void GeneratesAndPinsTheP3FixtureExamples()
    {
        var dir = Path.Combine(RepoRoot(), "docs", "examples");
        Directory.CreateDirectory(dir);
        var key = "synthetic-fixture-secret-not-a-real-key"u8.ToArray();
        var trust = new RendererTrustContext(RendererTransport.local_loopback);

        // 1. Non-redacted: an ordinary persistence-safe plan.
        var plainV3 = V2Translation.FromV2(SyntheticPlain());
        var plain = V3ShadowEnvelopeBuilder.Build(SyntheticPlain(), plainV3, key, 1, ["usr-local"], trust);
        Assert.False(plain.ContainsProtected);
        Assert.NotNull(plain.RenderPromptHash);
        File.WriteAllText(Path.Combine(dir, "v3-shadow-fixture-plain.json"),
            JsonSerializer.Serialize(plain, Pretty) + "\n");

        // 2. Redacted: a restricted, volatile, third-party-owned item.
        var protectedV3 = plainV3 with
        {
            Items = [.. plainV3.Items.Select((i, n) => n == 0
                ? i with
                {
                    Text = "A synthetic relative's synthetic scan results arrive on a synthetic Tuesday.",
                    Disclosure = Disclosure.restricted,
                    Owner = "principal:synthetic-relative",
                    Audience = ["usr-local"],
                    Retention = Retention.volatile_turn_only,
                }
                : i)],
        };
        var redacted = V3ShadowEnvelopeBuilder.Build(SyntheticPlain(), protectedV3, key, 1, ["usr-local"], trust);
        Assert.True(redacted.ContainsProtected);
        Assert.Null(redacted.RenderPromptHash);
        Assert.StartsWith("v1:", redacted.CorrelationTag);
        var json = JsonSerializer.Serialize(redacted, Pretty);
        Assert.DoesNotContain("scan results", json);
        File.WriteAllText(Path.Combine(dir, "v3-shadow-fixture-redacted.json"), json + "\n");
    }
}
