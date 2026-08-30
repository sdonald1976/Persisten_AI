using Companion.Core;
using System.Security.Cryptography;
using System.Text;

namespace Companion.Infrastructure.Models.Bootstrap;

/// <summary>What the bootstrap was asked to do. Modes are exclusive; selection is orthogonal.</summary>
public enum BootstrapMode
{
    /// <summary>Acquire whatever is missing, then let startup continue.</summary>
    Normal,

    /// <summary>Report what would be checked and acquired. Touches nothing.</summary>
    DryRun,

    /// <summary>Check only. Downloads nothing, and FAILS if anything required is missing or invalid.</summary>
    VerifyOnly,
}

/// <summary>Per-artifact verdict. Ordered so the worst state sorts last in a report.</summary>
public enum ArtifactState
{
    /// <summary>Present and its pinned hash matched.</summary>
    Verified,

    /// <summary>Present, and the strongest check available passed — but nothing pins it.</summary>
    PresentUnpinned,

    /// <summary>Not present. Acquirable.</summary>
    Missing,

    /// <summary>Present but wrong: hash mismatch, or a Git LFS pointer where weights belong.</summary>
    Invalid,

    /// <summary>Missing, and nothing here can fetch it — no source metadata, or a build-only artifact.</summary>
    Unacquirable,

    /// <summary>A prerequisite tool or credential is absent, so the check could not be made.</summary>
    Blocked,
}

/// <summary>One dependency's outcome, with the human-actionable next step when there is one.</summary>
public sealed record ArtifactResult
{
    public required ModelDependency Dependency { get; init; }
    public required ArtifactState State { get; init; }

    /// <summary>What was found, in one line. Never contains a token, key, or credentialed URL.</summary>
    public required string Detail { get; init; }

    /// <summary>What the operator should do. Null when nothing is needed.</summary>
    public string? Action { get; init; }

    /// <summary>Set when this run acquired the artifact.</summary>
    public bool Acquired { get; init; }

    public bool IsFailure => State is ArtifactState.Missing or ArtifactState.Invalid
        or ArtifactState.Unacquirable or ArtifactState.Blocked;
}

/// <summary>The whole run. <see cref="Ok"/> is the only thing startup needs to consult.</summary>
public sealed record BootstrapReport
{
    public required IReadOnlyList<ArtifactResult> Results { get; init; }
    public required BootstrapMode Mode { get; init; }
    public required string RendererRouting { get; init; }
    public IReadOnlyList<string> PrerequisiteProblems { get; init; } = [];

    /// <summary>Only ACTIVE dependencies can fail the run. An inactive model is not a problem.</summary>
    public bool Ok => !Results.Any(r => r.Dependency.Active && r.IsFailure);
}

/// <summary>
/// A locally served adapter, asked what it loaded.
///
/// Behind an interface for the same reason the others are: a bootstrap test must be able to
/// describe a healthy endpoint, a wrong-adapter endpoint and a dead one without any of them
/// existing.
/// </summary>
public interface IServedAdapterProbe
{
    /// <summary>
    /// The adapter hash the endpoint reports, or null when it cannot be reached. The second
    /// return value carries the reason, which is the only thing worth telling a human when the
    /// answer is "no".
    /// </summary>
    Task<(string? AdapterSha256, string Detail)> IdentifyAsync(
        string baseUrl, CancellationToken ct = default);
}

/// <summary>The Ollama CLI/server, behind an interface so tests never shell out or download.</summary>
public interface IOllamaClient
{
    /// <summary>Whether the `ollama` executable is on PATH.</summary>
    bool IsInstalled { get; }

    /// <summary>Tags the server currently has, or null if it could not be reached.</summary>
    Task<IReadOnlySet<string>?> ListAsync(CancellationToken ct = default);

    /// <summary>`ollama pull &lt;tag&gt;`. Returns false with a reason on failure.</summary>
    Task<(bool Ok, string Detail)> PullAsync(string tag, CancellationToken ct = default);
}

/// <summary>Hugging Face / direct downloads, behind an interface for the same reason.</summary>
public interface IArtifactDownloader
{
    bool IsAvailable { get; }

    /// <summary>Why it is unavailable (missing python, missing huggingface_hub, no auth).</summary>
    string? UnavailableReason { get; }

    Task<(bool Ok, string Detail)> DownloadAsync(
        ArtifactSource source, string destination, CancellationToken ct = default);
}

/// <summary>Git LFS, behind an interface so pointer handling is testable offline.</summary>
public interface IGitLfsClient
{
    bool IsInstalled { get; }

    Task<(bool Ok, string Detail)> PullAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Decides what a machine is missing and — outside verify/dry-run — gets it.
///
/// Every external effect goes through one of the three injected clients, so the whole decision
/// surface is exercised offline in tests. Nothing here reads configuration: it is handed the
/// dependencies that <see cref="ModelDependencies.Discover"/> derived from the app's own typed
/// options, which is what keeps a second roster from existing.
/// </summary>
public sealed class ModelBootstrap(
    IOllamaClient ollama,
    IArtifactDownloader downloader,
    IGitLfsClient lfs,
    IFileSystem files,
    IServedAdapterProbe? servedProbe = null)
{
    /// <summary>
    /// Git LFS writes a small text stub in place of the real file when content was not fetched.
    /// A fresh clone without LFS therefore has an adapter file of the right NAME and entirely the
    /// wrong contents — which existence checks pass and models fail on.
    /// </summary>
    public const string LfsPointerMagic = "version https://git-lfs.github.com/spec/v1";

    public async Task<BootstrapReport> RunAsync(
        IReadOnlyList<ModelDependency> dependencies,
        BootstrapMode mode,
        string rendererRouting,
        bool allConfigured = false,
        IReadOnlySet<string>? forceIds = null,
        CancellationToken ct = default)
    {
        var selected = allConfigured
            ? dependencies
            : dependencies.Where(d => d.Active).ToList();

        var problems = new List<string>();
        var results = new List<ArtifactResult>();

        // One catalog fetch for every Ollama-served dependency, not one per role.
        IReadOnlySet<string>? catalog = null;
        var wantsOllama = selected.Any(d =>
            d.Kind is DependencyKind.OllamaModel or DependencyKind.LocallyBuiltOllamaModel);
        if (wantsOllama)
        {
            if (!ollama.IsInstalled)
                problems.Add("ollama is not on PATH. Install it from https://ollama.com/download, "
                             + "then re-run. Nothing else can supply these models.");
            else
            {
                catalog = await ollama.ListAsync(ct);
                if (catalog is null)
                    problems.Add("ollama is installed but its server could not be reached. "
                                 + "Start it with `ollama serve` and re-run.");
            }
        }

        foreach (var dep in selected)
        {
            var force = forceIds?.Contains(dep.Id) == true;
            results.Add(dep.Kind switch
            {
                DependencyKind.OllamaModel =>
                    await CheckOllamaAsync(dep, catalog, mode, force, ct),
                DependencyKind.LocallyBuiltOllamaModel =>
                    CheckLocallyBuilt(dep, catalog),
                DependencyKind.LocalFile =>
                    await CheckLocalFileAsync(dep, mode, force, ct),
                DependencyKind.HuggingFaceSnapshot =>
                    await CheckSnapshotAsync(dep, mode, force, ct),
                DependencyKind.HttpServedAdapter =>
                    await CheckHttpServedAsync(dep, ct),
                _ => new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Blocked,
                    Detail = $"unsupported dependency kind {dep.Kind}",
                },
            });
        }

        return new BootstrapReport
        {
            Results = results,
            Mode = mode,
            RendererRouting = rendererRouting,
            PrerequisiteProblems = problems,
        };
    }

    // ---- Ollama tags --------------------------------------------------------------------------

    private async Task<ArtifactResult> CheckOllamaAsync(
        ModelDependency dep, IReadOnlySet<string>? catalog, BootstrapMode mode, bool force,
        CancellationToken ct)
    {
        if (!ollama.IsInstalled)
            return Blocked(dep, "ollama is not installed",
                "Install Ollama from https://ollama.com/download");

        if (catalog is null)
            return Blocked(dep, "the Ollama server could not be reached", "Run `ollama serve`");

        var present = Serves(catalog, dep.Identifier);
        if (present && !force)
            // No pinned digest exists for an Ollama tag in this configuration, and saying so is
            // the point: "the server has this tag" is the strongest check available, not a hash.
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = "served by Ollama (no pinned hash in configuration; tag presence is the strongest available check)",
            };

        if (mode == BootstrapMode.VerifyOnly)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = "not served by Ollama",
                Action = $"ollama pull {dep.Identifier}",
            };

        if (mode == BootstrapMode.DryRun)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = force ? "would be re-pulled (-Force)" : "would be pulled",
                Action = $"ollama pull {dep.Identifier}",
            };

        var (ok, detail) = await ollama.PullAsync(dep.Identifier, ct);
        return ok
            ? new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned, Acquired = true,
                Detail = $"pulled ({detail})",
            }
            : new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = $"pull failed: {detail}",
                Action = $"ollama pull {dep.Identifier}",
            };
    }

    /// <summary>Ollama's catalog lists "name:tag"; a bare name means the implicit :latest.</summary>
    private static bool Serves(IReadOnlySet<string> catalog, string model)
        => catalog.Contains(model)
           || (!model.Contains(':') && catalog.Contains(model + ":latest"));

    /// <summary>
    /// Ask the endpoint what it loaded, and compare that to the pin.
    ///
    /// Nothing here can acquire the dependency - a serving process is started, not downloaded -
    /// so the useful outcomes are "it is serving the weights we expect", "it is serving something
    /// else", and "it is not running, here is the command". The middle one matters most: a healthy
    /// process serving the wrong adapter passes every check that only asks whether it responds.
    /// </summary>
    private async Task<ArtifactResult> CheckHttpServedAsync(
        ModelDependency dep, CancellationToken ct)
    {
        var start = dep.Source?.BuiltBy is { Length: > 0 } script
            ? $"Start it: python {script}"
            : "Start the serving process.";

        if (string.IsNullOrWhiteSpace(dep.BaseUrl))
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Blocked,
                Detail = "no endpoint configured", Action = start,
            };

        if (servedProbe is null)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = "no probe configured; the endpoint was not contacted",
            };

        var (loaded, detail) = await servedProbe.IdentifyAsync(dep.BaseUrl, ct);
        if (loaded is null)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = detail, Action = start,
            };

        if (string.IsNullOrWhiteSpace(dep.Sha256))
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = $"serving {Short(loaded)} (no pin configured to check it against)",
            };

        // A healthy process serving the WRONG adapter passes every check that only asks whether
        // it responds, which is why this comparison is the point of the whole probe.
        return string.Equals(dep.Sha256, loaded, StringComparison.OrdinalIgnoreCase)
            ? new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Verified,
                Detail = $"endpoint is serving the pinned adapter ({Short(loaded)})",
            }
            : new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Invalid,
                Detail = $"endpoint is serving {Short(loaded)}, pin is {Short(dep.Sha256)}",
                Action = "Restart the endpoint against the pinned adapter, or correct the pin.",
            };
    }

    private static ArtifactResult CheckLocallyBuilt(ModelDependency dep, IReadOnlySet<string>? catalog)
    {
        if (catalog is not null && Serves(catalog, dep.Identifier))
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = $"served by Ollama as '{dep.Identifier}' (built locally; no registry to verify against)",
            };

        // Deliberately never "pulled": no registry has it, and pulling a same-named public model
        // would be a silent substitution of a different mouth.
        return new ArtifactResult
        {
            Dependency = dep, State = ArtifactState.Unacquirable,
            Detail = $"'{dep.Identifier}' is not served by Ollama and cannot be downloaded — it is built locally",
            Action = $"Build it: python {dep.Source?.BuiltBy ?? "tools/build_renderer_model.py"}",
        };
    }

    // ---- files on disk ------------------------------------------------------------------------

    private async Task<ArtifactResult> CheckLocalFileAsync(
        ModelDependency dep, BootstrapMode mode, bool force, CancellationToken ct)
    {
        var path = dep.ExpectedPath;
        if (string.IsNullOrWhiteSpace(path))
            return Blocked(dep, "no expected path could be resolved", null);

        var exists = files.FileExists(path);

        // A pointer is worse than absence: the name is right, the size is tiny, and every
        // existence check in the world says yes.
        if (exists && await IsLfsPointerAsync(path, ct))
        {
            if (mode is BootstrapMode.VerifyOnly or BootstrapMode.DryRun || !lfs.IsInstalled)
                return new ArtifactResult
                {
                    Dependency = dep, State = lfs.IsInstalled ? ArtifactState.Invalid : ArtifactState.Blocked,
                    Detail = "file is a Git LFS pointer, not the artifact",
                    Action = lfs.IsInstalled
                        ? $"git lfs pull --include {dep.Identifier}"
                        : "Install Git LFS (https://git-lfs.com), then `git lfs install && git lfs pull`",
                };

            var (pulled, pullDetail) = await lfs.PullAsync(path, ct);
            if (!pulled)
                return new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Invalid,
                    Detail = $"Git LFS pointer; restore failed: {pullDetail}",
                    Action = $"git lfs pull --include {dep.Identifier}",
                };
            exists = files.FileExists(path);
        }

        if (!exists || force)
        {
            var missingDetail = force ? "re-acquiring (-Force)" : "file not found";
            if (dep.Source is not { CanAcquire: true })
                return new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Unacquirable,
                    Detail = $"{missingDetail}; configuration records no source for it",
                    Action = SourceAdviceFor(dep),
                };
            if (mode == BootstrapMode.VerifyOnly)
                return new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Missing, Detail = missingDetail,
                    Action = DescribeSource(dep.Source),
                };
            if (mode == BootstrapMode.DryRun)
                return new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Missing,
                    Detail = $"{missingDetail}; would download",
                    Action = DescribeSource(dep.Source),
                };
            if (!downloader.IsAvailable)
                return Blocked(dep, downloader.UnavailableReason ?? "no downloader available",
                    "Install Python and `pip install huggingface_hub`");

            var (ok, detail) = await downloader.DownloadAsync(dep.Source, path, ct);
            if (!ok)
                return new ArtifactResult
                {
                    Dependency = dep, State = ArtifactState.Missing,
                    Detail = $"download failed: {detail}", Action = DescribeSource(dep.Source),
                };
        }

        return await VerifyFileAsync(dep, path, ct);
    }

    private async Task<ArtifactResult> VerifyFileAsync(
        ModelDependency dep, string path, CancellationToken ct)
    {
        var missingCompanions = dep.CompanionFiles.Where(f => !files.FileExists(f)).ToList();
        if (missingCompanions.Count > 0)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Invalid,
                Detail = $"{missingCompanions.Count} required file(s) missing beside it: "
                         + string.Join(", ", missingCompanions.Select(Path.GetFileName)),
                Action = "Restore the full artifact directory",
            };

        if (string.IsNullOrWhiteSpace(dep.Sha256))
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = "present; NO pinned SHA-256 in configuration, so only existence was checked",
            };

        var actual = await Sha256Async(path, ct);
        return string.Equals(actual, dep.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Verified,
                Detail = $"sha256 matches the configured pin ({Short(actual)})",
            }
            : new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Invalid,
                Detail = $"sha256 MISMATCH: configured {Short(dep.Sha256)}, found {Short(actual)}",
                Action = $"Re-acquire it, or correct the pin if the artifact legitimately changed",
            };
    }

    // ---- snapshots ----------------------------------------------------------------------------

    private async Task<ArtifactResult> CheckSnapshotAsync(
        ModelDependency dep, BootstrapMode mode, bool force, CancellationToken ct)
    {
        var path = dep.ExpectedPath;
        if (!string.IsNullOrWhiteSpace(path) && files.DirectoryExists(path) && !force)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned,
                Detail = dep.Source?.Revision is { } rev
                    ? $"snapshot directory present (pinned revision {Short(rev)}; per-file hashes not re-verified)"
                    : "snapshot directory present (no revision pinned)",
            };

        if (dep.Source is not { CanAcquire: true })
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Unacquirable,
                Detail = "not present and configuration records no source",
                Action = SourceAdviceFor(dep),
            };

        if (mode is BootstrapMode.VerifyOnly or BootstrapMode.DryRun)
            return new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = mode == BootstrapMode.DryRun ? "would download" : "not present",
                Action = DescribeSource(dep.Source),
            };

        if (!downloader.IsAvailable)
            return Blocked(dep, downloader.UnavailableReason ?? "no downloader available",
                "Install Python and `pip install huggingface_hub`");

        var (ok, detail) = await downloader.DownloadAsync(dep.Source, path ?? "", ct);
        return ok
            ? new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.PresentUnpinned, Acquired = true,
                Detail = $"downloaded ({detail})",
            }
            : new ArtifactResult
            {
                Dependency = dep, State = ArtifactState.Missing,
                Detail = $"download failed: {detail}", Action = DescribeSource(dep.Source),
            };
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<bool> IsLfsPointerAsync(string path, CancellationToken ct)
    {
        // A pointer is ~130 bytes; reading a prefix is enough and never loads a 4 GB model.
        var head = await files.ReadHeadAsync(path, 256, ct);
        return head.StartsWith(LfsPointerMagic, StringComparison.Ordinal);
    }

    private async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = files.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Source description for an operator. Prints repository, revision and file — and never the
    /// URI, because a direct URI is the one field that can carry a credential in a query string.
    /// </summary>
    private static string DescribeSource(ArtifactSource source)
    {
        var parts = new List<string>();
        if (source.Repository is { } repo)
            parts.Add($"huggingface {repo}");
        if (source.Revision is { } rev)
            parts.Add($"@{Short(rev)}");
        if (source.File is { } file)
            parts.Add($"file {file}");
        if (source.Repository is null && source.Uri is not null)
            parts.Add("a configured direct URI (not printed)");
        return parts.Count == 0 ? "no source recorded" : "Download from " + string.Join(" ", parts);
    }

    private static string SourceAdviceFor(ModelDependency dep)
        => dep.Source?.BuiltBy is { } script
            ? $"Build it: python {script}"
            : $"Add Source (Repository/Revision/File or Uri) for '{dep.Id}' in configuration — "
              + "a repository is never guessed from a filename.";

    private static ArtifactResult Blocked(ModelDependency dep, string detail, string? action)
        => new() { Dependency = dep, State = ArtifactState.Blocked, Detail = detail, Action = action };

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];
}

/// <summary>File access behind an interface so the whole decision surface is testable offline.</summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    Stream OpenRead(string path);
    Task<string> ReadHeadAsync(string path, int bytes, CancellationToken ct = default);
}

/// <summary>The real disk.</summary>
public sealed class LocalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public async Task<string> ReadHeadAsync(string path, int bytes, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[bytes];
        var read = await stream.ReadAsync(buffer.AsMemory(), ct);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
