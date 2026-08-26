using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// Byte goldens for <c>CompactV3</c>, over the same frozen corpus the plan/2 golden uses.
///
/// The audit found plan/2 well covered — 804 plans through the producer hop, 289 against
/// their frozen strings — and CompactV3 covered only by substring and negative assertions.
/// Nothing pinned its actual bytes, so a refactor could change every V3 rendering in the
/// repository and the suite would stay green.
///
/// The manifest is id → sha256 rather than 804 embedded strings: a hash file fails on any
/// single byte of drift and names the plan that drifted, which is what a refactor needs,
/// while staying small enough to review in a diff. A handful of full renderings are pinned
/// verbatim beside it so the format itself is legible without running anything.
///
/// This is deliberately NOT a second plan/2 golden. It runs the same corpus through a
/// different serializer, which is the coverage that was missing.
/// </summary>
public class CompactV3GoldenTests
{
    public static string ManifestPath => Path.Combine(
        CorpusGoldenTests.RepoRoot(), "tools", "Companion.PlanV3.Prototype",
        "Goldens", "compact-v3-manifest.txt");

    public static string SamplesPath => Path.Combine(
        CorpusGoldenTests.RepoRoot(), "tools", "Companion.PlanV3.Prototype",
        "Goldens", "compact-v3-samples.txt");

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Every corpus plan that survives translation, as id → CompactV3 hash.</summary>
    private static SortedDictionary<string, string> CurrentManifest()
    {
        var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, plan, _) in CorpusGoldenTests.CorpusPlans())
        {
            var v3 = V2Translation.FromV2(plan);
            if (PlanV3Codec.Validate(v3).Count > 0)
                continue;                       // the plan/2 golden already owns validity

            // CompactV3 enforces more than Validate does — the coaching lint runs at
            // serialization time and REFUSES a plan whose producer-authored text coaches.
            // Which plans it refuses is itself a behaviour worth pinning: a refactor that
            // quietly started accepting them would otherwise pass unnoticed.
            try
            {
                manifest[id] = Sha256(PlanV3Codec.CompactV3(v3));
            }
            catch (InvalidOperationException)
            {
                manifest[id] = "refused-by-lint";
            }
        }
        return manifest;
    }

    [Fact]
    public void CompactV3_IsByteIdentical_AcrossTheEntireFrozenCorpus()
    {
        var current = CurrentManifest();
        Assert.True(current.Count >= 800,
            $"expected the whole corpus to translate; saw {current.Count}");

        Assert.True(File.Exists(ManifestPath),
            $"golden manifest missing at {ManifestPath}. Generate it deliberately and commit "
            + "it as its own reviewed change — a golden that regenerates itself proves nothing.");

        var expected = File.ReadAllLines(ManifestPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);

        var drifted = current
            .Where(kv => !expected.TryGetValue(kv.Key, out var hash) || hash != kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        var vanished = expected.Keys.Except(current.Keys).ToList();

        Assert.True(drifted.Count == 0,
            $"{drifted.Count}/{current.Count} plans render differently through CompactV3:\n"
            + string.Join("\n", drifted.Take(12)));
        Assert.True(vanished.Count == 0,
            $"{vanished.Count} plans no longer translate at all:\n"
            + string.Join("\n", vanished.Take(12)));
    }

    [Fact]
    public void TheSampleRenderings_AreUnchanged()
    {
        // Full text for a small stable slice, so the format is reviewable in a diff rather
        // than only detectable as a changed hash.
        Assert.True(File.Exists(SamplesPath), $"golden samples missing at {SamplesPath}");

        var expected = File.ReadAllText(SamplesPath).ReplaceLineEndings("\n");
        var actual = RenderSamples().ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);
    }

    /// <summary>The first ten corpus plans by id, rendered in full.</summary>
    public static string RenderSamples()
    {
        var sb = new StringBuilder();
        sb.Append("# CompactV3 golden samples. Regenerate only as a reviewed change.\n");
        foreach (var (id, plan, _) in CorpusGoldenTests.CorpusPlans()
                     .OrderBy(p => p.Id, StringComparer.Ordinal)
                     .Where(p => Renderable(p.Plan))
                     .Take(10))
        {
            sb.Append("\n===== ").Append(id).Append(" =====\n");
            sb.Append(PlanV3Codec.CompactV3(V2Translation.FromV2(plan)).ReplaceLineEndings("\n"));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool Renderable(Companion.Core.Domain.ResponsePlan plan)
    {
        var v3 = V2Translation.FromV2(plan);
        if (PlanV3Codec.Validate(v3).Count > 0)
            return false;
        try { PlanV3Codec.CompactV3(v3); return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The manifest text, for the generator below.</summary>
    public static string RenderManifest()
    {
        var sb = new StringBuilder();
        sb.Append("# id sha256(CompactV3). Regenerate only as a reviewed change.\n");
        foreach (var (id, hash) in CurrentManifest())
            sb.Append(id).Append(' ').Append(hash).Append('\n');
        return sb.ToString();
    }
}
