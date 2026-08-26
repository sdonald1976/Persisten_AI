using Companion.Core;
using System.Diagnostics;
using System.Text.Json;

namespace Companion.Infrastructure.Models.Bootstrap;

/// <summary>
/// Running an external command, in one place, so nothing else in the bootstrap has to think
/// about redirection, timeouts, or the fact that a hung `ollama pull` should not hang startup.
/// </summary>
internal static class Proc
{
    public static bool OnPath(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
                return false;
            p.WaitForExit(10_000);
            return true;
        }
        catch
        {
            // Win32Exception when the executable is not on PATH. Absence is an answer, not a fault.
            return false;
        }
    }

    public static async Task<(int Code, string Output)> RunAsync(
        string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, $"timed out after {timeout.TotalSeconds:F0}s");
        }

        var output = ((await stdout) + "\n" + (await stderr)).Trim();
        return (p.ExitCode, output);
    }
}

/// <summary>The real Ollama, via its CLI and HTTP catalog.</summary>
public sealed class OllamaCliClient(HttpClient http, string baseUrl) : IOllamaClient
{
    private bool? _installed;

    public bool IsInstalled => _installed ??= Proc.OnPath("ollama");

    public async Task<IReadOnlySet<string>?> ListAsync(CancellationToken ct = default)
    {
        // The HTTP catalog rather than parsing CLI table output: it is the same source the
        // application's own preflight uses, so both agree about what "served" means.
        try
        {
            var root = baseUrl.Replace("/v1", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            using var response = await http.GetAsync($"{root}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("models", out var models))
                return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in models.EnumerateArray())
                if (m.TryGetProperty("name", out var name) && name.GetString() is { } s)
                    names.Add(s);
            return names;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<(bool Ok, string Detail)> PullAsync(string tag, CancellationToken ct = default)
    {
        // Generous: a cold 8B pull on a slow link genuinely takes this long.
        var (code, output) = await Proc.RunAsync("ollama", $"pull {tag}", TimeSpan.FromMinutes(60), ct);
        return code == 0 ? (true, "ok") : (false, LastLine(output));
    }

    private static string LastLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "no output" : lines[^1];
    }
}

/// <summary>The real Git LFS.</summary>
public sealed class GitLfsCliClient(string repositoryRoot) : IGitLfsClient
{
    private bool? _installed;

    public bool IsInstalled => _installed ??= Proc.OnPath("git-lfs");

    public async Task<(bool Ok, string Detail)> PullAsync(string path, CancellationToken ct = default)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        var (code, output) = await Proc.RunAsync(
            "git", $"-C \"{repositoryRoot}\" lfs pull --include \"{relative}\"",
            TimeSpan.FromMinutes(30), ct);
        return code == 0 ? (true, "restored") : (false, output.Split('\n').LastOrDefault() ?? "failed");
    }
}

/// <summary>
/// Hugging Face downloads through the official CLI. Only ever invoked with a repository and
/// revision that configuration stated explicitly — this class never derives one from a filename.
/// </summary>
public sealed class HuggingFaceDownloader : IArtifactDownloader
{
    private readonly Lazy<(bool Ok, string? Why)> _probe = new(Probe);

    public bool IsAvailable => _probe.Value.Ok;

    public string? UnavailableReason => _probe.Value.Why;

    private static (bool, string?) Probe()
    {
        if (!Proc.OnPath("python") && !Proc.OnPath("python3"))
            return (false, "python is not on PATH (needed for huggingface_hub)");
        var (code, _) = Proc.RunAsync(
            "python", "-c \"import huggingface_hub\"", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        return code == 0
            ? (true, null)
            : (false, "the huggingface_hub package is not installed (`pip install huggingface_hub`)");
    }

    public async Task<(bool Ok, string Detail)> DownloadAsync(
        ArtifactSource source, string destination, CancellationToken ct = default)
    {
        if (source.Repository is null)
            return (false, "no repository configured; a repository is never guessed");

        var revision = source.Revision is { } r ? $", revision='{r}'" : "";
        var script = source.File is { } file
            ? $"from huggingface_hub import hf_hub_download; "
              + $"hf_hub_download('{source.Repository}', '{file}'{revision}, local_dir=r'{Path.GetDirectoryName(destination)}')"
            : $"from huggingface_hub import snapshot_download; "
              + $"snapshot_download('{source.Repository}'{revision}, local_dir=r'{destination}')";

        var (code, output) = await Proc.RunAsync(
            "python", $"-c \"{script.Replace("\"", "\\\"")}\"", TimeSpan.FromHours(2), ct);

        if (code == 0)
            return (true, source.Revision is { } rev ? $"revision {rev[..Math.Min(12, rev.Length)]}" : "latest");

        // Gated repositories fail with a recognizable shape; say what to do rather than dumping
        // a stack trace that may contain a token-bearing URL.
        var lower = output.ToLowerInvariant();
        if (lower.Contains("401") || lower.Contains("gated") || lower.Contains("authentication"))
            return (false, $"repository {source.Repository} appears to be gated — run `huggingface-cli login`");
        return (false, $"download of {source.Repository} failed (exit {code})");
    }
}
