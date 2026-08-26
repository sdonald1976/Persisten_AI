using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Companion.Tests;

namespace Companion.Goldens;

/// <summary>
/// Deliberate golden regeneration. Refuses to write anything without <c>--accept</c>.
///
/// The goldens exist because a refactor must prove that serialized bytes, rendered prompts
/// and the EF model did not change. A tool that rewrites them casually destroys exactly that
/// value, so this one is built to be inconvenient in the right way: it is a separate
/// executable with no test attributes anywhere, it reports before it writes, and the write
/// requires a flag that a human has to type.
///
/// Provenance (protocol version, source commit, dirty state) is recorded in a sidecar
/// PROVENANCE.txt rather than inside the golden files themselves, because embedding a commit
/// hash in a golden would make its bytes change on every commit — which would defeat the
/// only thing a golden is for.
/// </summary>
internal static class Program
{
    private sealed record Golden(string Name, string Path, Func<Task<string>> RenderAsync);

    private static IReadOnlyList<Golden> All() =>
    [
        new("compact-v3-manifest",
            Companion.PlanV3.CompactV3GoldenTests.ManifestPath,
            () => Task.FromResult(Companion.PlanV3.CompactV3GoldenTests.RenderManifest())),
        new("compact-v3-samples",
            Companion.PlanV3.CompactV3GoldenTests.SamplesPath,
            () => Task.FromResult(Companion.PlanV3.CompactV3GoldenTests.RenderSamples())),
        new("compact-v4",
            CompactV4GoldenTests.GoldenPath,
            () => Task.FromResult(CompactV4GoldenTests.Render())),
        new("prompt-render",
            PromptRenderGoldenTests.GoldenPath,
            () => Task.FromResult(PromptRenderGoldenTests.Render())),
        new("ef-model",
            EfModelSnapshotTests.SnapshotPath,
            EfModelSnapshotTests.CurrentAsync),
    ];

    private static async Task<int> Main(string[] args)
    {
        var accept = args.Contains("--accept", StringComparer.Ordinal);
        var only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))
            ?["--only=".Length..];

        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            Console.WriteLine(
                """
                goldens — report or regenerate the refactor safety-net goldens.

                  goldens                  report drift; writes nothing
                  goldens --accept         rewrite the drifted goldens
                  goldens --only=NAME      restrict to one golden
                  goldens --help

                Regeneration is never automatic. It is not wired into the build, the test
                run, or CI, and it will not write without --accept.
                """);
            return 0;
        }

        // Ordering is fixed by the list above, never by directory enumeration, so two runs
        // on two machines produce the same report and the same bytes.
        var goldens = All()
            .Where(g => only is null || g.Name == only)
            .ToList();
        if (goldens.Count == 0)
        {
            Console.Error.WriteLine($"no golden named '{only}'. Known: "
                + string.Join(", ", All().Select(g => g.Name)));
            return 2;
        }

        var drifted = new List<(Golden Golden, string Current, string? OnDisk)>();
        Console.WriteLine($"{"golden",-22} {"status",-10} {"lines",6}  {"sha256",-16}");
        Console.WriteLine(new string('-', 72));

        foreach (var golden in goldens)
        {
            // Line endings are normalised on write AND on compare, so a checkout with
            // different autocrlf settings does not read as drift.
            var current = Normalise(await golden.RenderAsync());
            var onDisk = File.Exists(golden.Path) ? Normalise(File.ReadAllText(golden.Path)) : null;

            var status = onDisk is null ? "MISSING"
                : onDisk == current ? "unchanged"
                : "DRIFTED";
            Console.WriteLine(
                $"{golden.Name,-22} {status,-10} {LineCount(current),6}  {Sha(current)[..16]}");

            if (status != "unchanged")
                drifted.Add((golden, current, onDisk));
        }

        Console.WriteLine();
        if (drifted.Count == 0)
        {
            Console.WriteLine("All goldens match. Nothing to do.");
            return 0;
        }

        foreach (var (golden, current, onDisk) in drifted)
            ReportDiff(golden, current, onDisk);

        if (!accept)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{drifted.Count} golden(s) differ. NOTHING WAS WRITTEN.");
            Console.WriteLine(
                "Review the summary above. If every difference is intended, re-run with --accept.");
            // Non-zero so a human running this by hand cannot mistake drift for success.
            return 1;
        }

        foreach (var (golden, current, _) in drifted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden.Path)!);
            // Explicit UTF-8 without BOM and \n endings: the bytes must not depend on the
            // machine that produced them.
            File.WriteAllText(golden.Path, current, new UTF8Encoding(false));
            Console.WriteLine($"wrote {golden.Path}");
            WriteProvenance(golden, current);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{drifted.Count} golden(s) rewritten. Commit them as their own reviewed change, "
            + "with the reason the bytes were expected to move.");
        return 0;
    }

    private static void ReportDiff(Golden golden, string current, string? onDisk)
    {
        Console.WriteLine($"=== {golden.Name} ===");
        if (onDisk is null)
        {
            Console.WriteLine("  no golden on disk; this would create it.");
            return;
        }

        var before = onDisk.Split('\n');
        var after = current.Split('\n');
        Console.WriteLine($"  lines {before.Length} -> {after.Length}");
        Console.WriteLine($"  sha   {Sha(onDisk)[..16]} -> {Sha(current)[..16]}");

        // A first-difference report rather than a full diff: these files run to hundreds of
        // lines and the first divergence is what tells you whether the change was intended.
        var shown = 0;
        for (var i = 0; i < Math.Max(before.Length, after.Length) && shown < 6; i++)
        {
            var b = i < before.Length ? before[i] : "(absent)";
            var a = i < after.Length ? after[i] : "(absent)";
            if (b == a)
                continue;
            Console.WriteLine($"  line {i + 1}:");
            Console.WriteLine($"    - {Trim(b)}");
            Console.WriteLine($"    + {Trim(a)}");
            shown++;
        }

        var totalChanged = before.Length == after.Length
            ? before.Zip(after).Count(p => p.First != p.Second)
            : -1;
        Console.WriteLine(totalChanged >= 0
            ? $"  {totalChanged} line(s) differ in total."
            : "  line counts differ; totals not comparable.");
        Console.WriteLine();
    }

    private static void WriteProvenance(Golden golden, string content)
    {
        var dir = Path.GetDirectoryName(golden.Path)!;
        var path = Path.Combine(dir, "PROVENANCE.txt");

        // Kept per-golden and OUT of the golden file itself: a commit hash inside a golden
        // would change its bytes on every commit, which is the opposite of what it is for.
        var existing = File.Exists(path)
            ? File.ReadAllLines(path)
                .Where(l => !l.StartsWith(golden.Name + " ", StringComparison.Ordinal))
                .ToList()
            : [];

        existing.RemoveAll(l => l.StartsWith("# ", StringComparison.Ordinal));
        var lines = new List<string>
        {
            "# Provenance for the goldens in this directory. Written by tools/Companion.Goldens.",
            "# Recorded here rather than inside the golden files, whose bytes must not depend",
            "# on which commit produced them.",
        };
        lines.AddRange(existing.Where(l => !string.IsNullOrWhiteSpace(l)));
        lines.Add($"{golden.Name} protocol={Protocol(golden.Name)} "
                  + $"commit={Commit()} sha256={Sha(content)} lines={LineCount(content)}");

        lines = [.. lines.Take(3), .. lines.Skip(3).OrderBy(l => l, StringComparer.Ordinal)];
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>The protocol a golden pins, so a version bump is visible in provenance.</summary>
    private static string Protocol(string name) => name switch
    {
        // PlanV3Codec exposes no Protocol constant the way PlanV4Codec does; the plan/3
        // header is emitted inline by CompactV3. Named literally here rather than pretending
        // to read it from a constant that does not exist.
        "compact-v3-manifest" or "compact-v3-samples" => "plan/3",
        "compact-v4" => Companion.PlanV3.PlanV4Codec.Protocol,
        "prompt-render" => "context-packet",
        "ef-model" => "ef-core-9",
        _ => "unknown",
    };

    private static string Commit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var p = Process.Start(psi);
            if (p is null) return "unknown";
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            if (p.ExitCode != 0 || sha.Length == 0) return "unknown";

            var dirty = new ProcessStartInfo("git", "status --porcelain")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var d = Process.Start(dirty);
            var changes = d?.StandardOutput.ReadToEnd() ?? "";
            d?.WaitForExit(5000);
            // A golden generated from an uncommitted tree is worth flagging: it cannot be
            // reproduced from the recorded commit alone.
            return changes.Trim().Length > 0 ? sha + "-dirty" : sha;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string Normalise(string text) => text.ReplaceLineEndings("\n");

    private static int LineCount(string text) => text.Split('\n').Length;

    private static string Sha(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Trim(string line)
        => line.Length <= 96 ? line : line[..96] + "…";
}
