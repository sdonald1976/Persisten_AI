using System.Text.Json;
using Companion.Core.Domain;
using Companion.RendererBench;
using Xunit;

namespace Companion.PlanV3;

/// <summary>
/// P2 golden comparisons across the COMPLETE frozen plan/2 corpus: every scenario plan in
/// train-200.jsonl (730), fixtures.jsonl (11), and the unseen families (32) must survive
/// the producer hop — FromV2 → Validate → TranslateToV2 → CompactV2 — byte-identically.
/// A second assertion pins deserialization fidelity: our CompactV2 of the parsed plan
/// equals the FROZEN plan2 string the corpus recorded. Any single byte of drift fails.
/// </summary>
public class CorpusGoldenTests
{
    private static readonly JsonSerializerOptions Loose = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "training", "renderer", "dataset", "train-200.jsonl")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    internal static IEnumerable<(string Id, ResponsePlan Plan, string? FrozenPlan2)> CorpusPlans()
    {
        var root = RepoRoot();

        var plan2ById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in new[]
                 {
                     Path.Combine(root, "training", "renderer", "dataset", "plan2-current.jsonl"),
                     Path.Combine(root, "training", "renderer", "unseen-plan2.jsonl"),
                 })
            foreach (var line in File.ReadLines(file))
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var doc = JsonDocument.Parse(line.TrimStart('﻿'));
                    plan2ById[doc.RootElement.GetProperty("id").GetString()!] =
                        doc.RootElement.GetProperty("plan2").GetString()!;
                }

        // The corpus's plan OBJECTS live in the scenario files; train-200 rows carry only
        // the frozen plan2 strings. Golden over EVERY scenario (761 ⊇ the 730 corpus).
        foreach (var file in Directory.GetFiles(
                     Path.Combine(root, "training", "renderer", "dataset", "scenarios"), "*.jsonl"))
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var doc = JsonDocument.Parse(line.TrimStart('﻿'));
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var plan = doc.RootElement.GetProperty("plan").Deserialize<ResponsePlan>(Loose)!;
                yield return (id, plan, plan2ById.GetValueOrDefault(id));
            }

        foreach (var file in Directory.GetFiles(Path.Combine(root, "training", "renderer", "unseen"), "*.jsonl"))
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var doc = JsonDocument.Parse(line.TrimStart('﻿'));
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var plan = doc.RootElement.GetProperty("plan").Deserialize<ResponsePlan>(Loose)!;
                yield return (id, plan, plan2ById.GetValueOrDefault(id));
            }

        foreach (var line in File.ReadLines(Path.Combine(root, "training", "renderer", "fixtures.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = JsonDocument.Parse(line.TrimStart('﻿'));
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var plan = doc.RootElement.GetProperty("plan").Deserialize<ResponsePlan>(Loose)!;
            yield return (id, plan, null);
        }
    }

    [Fact]
    public void TheProducerHop_IsByteIdentical_AcrossTheEntireFrozenCorpus()
    {
        var count = 0;
        var hingeFailures = new List<string>();
        var frozenFailures = new List<string>();

        foreach (var (id, plan, frozen) in CorpusPlans())
        {
            count++;
            var direct = PlanSerialization.CompactV2(plan);

            var v3 = V2Translation.FromV2(plan);
            var structural = PlanV3Codec.Validate(v3);
            if (structural.Count > 0)
            {
                hingeFailures.Add($"{id}: v3 invalid: {string.Join("; ", structural)}");
                continue;
            }
            var hop = PlanSerialization.CompactV2(V2Translation.TranslateToV2(v3));
            if (!string.Equals(direct, hop, StringComparison.Ordinal))
                hingeFailures.Add($"{id}: hop output differs from direct CompactV2");

            if (frozen is not null && !string.Equals(direct, frozen, StringComparison.Ordinal))
                frozenFailures.Add($"{id}: parsed-plan CompactV2 differs from the frozen plan2 string");
        }

        Assert.True(count >= 800, $"expected all scenarios+unseen+fixtures (761+32+11); saw {count}");
        Assert.True(hingeFailures.Count == 0,
            $"{hingeFailures.Count}/{count} hinge failures:\n" + string.Join("\n", hingeFailures.Take(12)));
        Assert.True(frozenFailures.Count == 0,
            $"{frozenFailures.Count} frozen-plan2 mismatches:\n" + string.Join("\n", frozenFailures.Take(12)));
    }
}
