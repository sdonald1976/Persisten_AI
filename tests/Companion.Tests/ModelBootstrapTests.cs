using System.Text;
using Companion.Core;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Models.Bootstrap;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The bootstrap's decisions, exercised entirely offline.
///
/// Every external effect — Ollama, Git LFS, Hugging Face, the disk — is behind an interface, and
/// these tests supply fakes for all four. Nothing here downloads a model, starts a process, or
/// touches the real filesystem; a test suite that needed a 5 GB pull to prove the bootstrap works
/// would be a worse version of the problem the bootstrap exists to solve.
/// </summary>
public class ModelBootstrapTests
{
    private const string Sha = "4732591a39e6aa078b87445e42c2e049cf1082009975345839e9604c7b36af2f";

    // ---- configuration discovery ---------------------------------------------------------------

    [Fact]
    public void Discovery_ReadsTheApplicationsOwnTypedOptions()
    {
        var deps = Discover();

        // The roles come from ModelDependencies.ProviderRoles, which ModelPreflight also uses —
        // there is no second roster to drift.
        Assert.Contains(deps, d => d.Id == "model.conversation" && d.Identifier == "chat-model");
        Assert.Contains(deps, d => d.Id == "model.embeddings" && d.Identifier == "embed-model");
        Assert.Contains(deps, d => d.Id == "renderer.adapter");
    }

    [Fact]
    public void Discovery_FollowsTheSameFallbackChainTheApplicationUses()
    {
        // Extraction is not configured, so the app uses Chat for it — and so must the bootstrap,
        // or it would pull a model nothing calls and miss one that everything does.
        var models = new ModelOptions
        {
            Provider = "OpenAiCompatible",
            Chat = new EndpointOptions { Model = "chat-model" },
            Embeddings = new EndpointOptions { Model = "embed-model" },
        };
        var deps = ModelDependencies.Discover(
            models, new CognitiveModelOptions(), new CompanionOptions(), new SafetyOptions(),
            "/models", "/repo");

        Assert.Equal("chat-model", deps.Single(d => d.Id == "model.extraction").Identifier);
    }

    [Fact]
    public void AMockProviderMakesEveryLanguageModelInactive()
    {
        var deps = ModelDependencies.Discover(
            new ModelOptions { Provider = "Mock" }, new CognitiveModelOptions(),
            new CompanionOptions(), new SafetyOptions(), "/models", "/repo");

        Assert.All(deps.Where(d => d.Kind == DependencyKind.OllamaModel),
            d => Assert.False(d.Active));
    }

    // ---- active versus merely configured --------------------------------------------------------

    [Fact]
    public async Task NormalStartupIgnoresModelsBelongingToDisabledCapabilities()
    {
        var ollama = new FakeOllama(installed: true, serving: ["chat-model", "embed-model"]);
        var report = await Run(ollama, Discover(), BootstrapMode.Normal);

        // The disabled ONNX models are not checked, not downloaded, and not a failure.
        Assert.DoesNotContain(report.Results, r => r.Dependency.Id == "cognitive.nli");
        Assert.Empty(ollama.Pulled);
    }

    [Fact]
    public async Task AllConfiguredIncludesTheDisabledOnes()
    {
        var report = await Run(
            new FakeOllama(installed: true, serving: ["chat-model", "embed-model"]),
            Discover(), BootstrapMode.DryRun, allConfigured: true);

        var nli = Assert.Single(report.Results, r => r.Dependency.Id == "cognitive.nli");
        Assert.False(nli.Dependency.Active);
    }

    [Fact]
    public async Task AnInactiveDependencyBeingAbsentDoesNotFailTheRun()
    {
        // Every disabled ONNX model is missing from the fake disk, and the renderer is off, so
        // the ONLY things wrong are things nothing uses. That must still be a clean startup.
        var models = new ModelOptions
        {
            Provider = "OpenAiCompatible",
            Chat = new EndpointOptions { Model = "chat-model" },
            Embeddings = new EndpointOptions { Model = "embed-model" },
        };
        var deps = ModelDependencies.Discover(
            models, Cognitive(), new CompanionOptions(), new SafetyOptions(), "/models", "/repo");

        var report = await Run(
            new FakeOllama(installed: true, serving: ["chat-model", "embed-model"]),
            deps, BootstrapMode.VerifyOnly, allConfigured: true);

        Assert.Contains(report.Results, r => !r.Dependency.Active && r.IsFailure);
        Assert.True(report.Ok);
    }

    // ---- idempotency ------------------------------------------------------------------------------

    [Fact]
    public async Task AnAlreadyServedModelIsNotPulledAgain()
    {
        var ollama = new FakeOllama(installed: true, serving: ["chat-model", "embed-model"]);
        await Run(ollama, Discover(), BootstrapMode.Normal);
        await Run(ollama, Discover(), BootstrapMode.Normal);

        Assert.Empty(ollama.Pulled);
    }

    [Fact]
    public async Task AVerifiedFileIsNotDownloadedAgain()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", RealWeights);
        var downloader = new FakeDownloader();

        var report = await Run(
            new FakeOllama(true, []), [Adapter(sha: ShaOf(RealWeights))], BootstrapMode.Normal,
            files: files, downloader: downloader);

        Assert.Equal(ArtifactState.Verified, report.Results[0].State);
        Assert.Empty(downloader.Requested);
    }

    // ---- verification ------------------------------------------------------------------------------

    [Fact]
    public async Task AHashMismatchIsInvalid_NotQuietlyAccepted()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", "the wrong weights entirely"u8.ToArray());

        var report = await Run(new FakeOllama(true, []), [Adapter(sha: Sha)], BootstrapMode.Normal, files: files);

        var result = report.Results[0];
        Assert.Equal(ArtifactState.Invalid, result.State);
        Assert.Contains("MISMATCH", result.Detail, StringComparison.Ordinal);
        Assert.False(report.Ok);
    }

    [Fact]
    public async Task AnUnpinnedArtifactSaysSoRatherThanClaimingVerification()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", "weights"u8.ToArray());

        var report = await Run(new FakeOllama(true, []), [Adapter(sha: null)], BootstrapMode.Normal, files: files);

        Assert.Equal(ArtifactState.PresentUnpinned, report.Results[0].State);
        Assert.Contains("NO pinned SHA-256", report.Results[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOllamaTagReportsThatTagPresenceIsTheStrongestCheckAvailable()
    {
        var report = await Run(
            new FakeOllama(true, ["chat-model", "embed-model"]), Discover(), BootstrapMode.Normal);

        var chat = Assert.Single(report.Results, r => r.Dependency.Id == "model.conversation");
        Assert.Equal(ArtifactState.PresentUnpinned, chat.State);
        Assert.Contains("no pinned hash", chat.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- LFS pointers --------------------------------------------------------------------------------

    [Fact]
    public async Task AnLfsPointerMasqueradingAsWeightsIsDetected()
    {
        // The exact failure a fresh clone without Git LFS produces: right name, right path,
        // 130 bytes of text where a 120 MB tensor belongs. Existence checks all pass.
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", Encoding.UTF8.GetBytes(
            ModelBootstrap.LfsPointerMagic + "\noid sha256:" + Sha + "\nsize 119801528\n"));

        var report = await Run(
            new FakeOllama(true, []), [Adapter(sha: Sha)], BootstrapMode.VerifyOnly, files: files);

        var result = report.Results[0];
        Assert.Equal(ArtifactState.Invalid, result.State);
        Assert.Contains("Git LFS pointer", result.Detail, StringComparison.Ordinal);
        Assert.Contains("git lfs pull", result.Action!, StringComparison.Ordinal);
        Assert.False(report.Ok);
    }

    [Fact]
    public async Task WithoutGitLfsInstalled_APointerIsBlockedWithInstallInstructions()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", Encoding.UTF8.GetBytes(ModelBootstrap.LfsPointerMagic + "\n"));

        var report = await Run(
            new FakeOllama(true, []), [Adapter(sha: Sha)], BootstrapMode.Normal,
            files: files, lfs: new FakeLfs(installed: false));

        Assert.Equal(ArtifactState.Blocked, report.Results[0].State);
        Assert.Contains("git-lfs.com", report.Results[0].Action!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InNormalMode_APointerIsRestoredThroughGitLfs()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", Encoding.UTF8.GetBytes(ModelBootstrap.LfsPointerMagic + "\n"));
        var lfs = new FakeLfs(installed: true, onPull: () => files.AddFile("/repo/adapter.safetensors", RealWeights));

        var report = await Run(
            new FakeOllama(true, []), [Adapter(sha: ShaOf(RealWeights))], BootstrapMode.Normal,
            files: files, lfs: lfs);

        Assert.Equal(ArtifactState.Verified, report.Results[0].State);
        Assert.Single(lfs.Pulled);
    }

    // ---- prerequisites and failures --------------------------------------------------------------------

    [Fact]
    public async Task AMissingOllamaIsAnActionableInstruction_NotAStackTrace()
    {
        var report = await Run(new FakeOllama(installed: false, serving: []), Discover(), BootstrapMode.Normal);

        Assert.Contains(report.PrerequisiteProblems, p => p.Contains("ollama.com/download", StringComparison.Ordinal));
        Assert.All(report.Results.Where(r => r.Dependency.Kind == DependencyKind.OllamaModel),
            r => Assert.Equal(ArtifactState.Blocked, r.State));
        Assert.False(report.Ok);
    }

    [Fact]
    public async Task AFailedPullFailsTheRunRatherThanSubstitutingAnotherModel()
    {
        var ollama = new FakeOllama(installed: true, serving: [], pullSucceeds: false);
        var report = await Run(ollama, Discover(), BootstrapMode.Normal);

        var chat = Assert.Single(report.Results, r => r.Dependency.Id == "model.conversation");
        Assert.Equal(ArtifactState.Missing, chat.State);
        Assert.Contains("pull failed", chat.Detail, StringComparison.Ordinal);
        Assert.False(report.Ok);

        // Nothing anywhere swapped in a different identifier.
        Assert.All(report.Results, r => Assert.NotEqual("", r.Dependency.Identifier));
    }

    [Fact]
    public async Task AMissingDownloaderIsBlockedWithWhatToInstall()
    {
        var report = await Run(
            new FakeOllama(true, []),
            [Adapter(sha: null) with
            {
                ExpectedPath = "/repo/missing.onnx",
                Source = new ArtifactSource { Repository = "org/repo", Revision = "abc123" },
            }],
            BootstrapMode.Normal,
            downloader: new FakeDownloader(available: false, reason: "python is not on PATH"));

        Assert.Equal(ArtifactState.Blocked, report.Results[0].State);
        Assert.Contains("huggingface_hub", report.Results[0].Action!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArtifactWithNoDeclaredSourceIsUnacquirable_NeverGuessedFromItsFilename()
    {
        var report = await Run(
            new FakeOllama(true, []),
            [Adapter(sha: null) with { ExpectedPath = "/repo/classifier.onnx", Source = null }],
            BootstrapMode.Normal);

        Assert.Equal(ArtifactState.Unacquirable, report.Results[0].State);
        Assert.Contains("never guessed", report.Results[0].Action!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLocallyBuiltRendererIsNeverPulledFromARegistry()
    {
        // Pulling a public model that happens to share the name would silently substitute a
        // different mouth. It must say "build it" instead.
        var ollama = new FakeOllama(installed: true, serving: []);
        var report = await Run(ollama, Discover(), BootstrapMode.Normal);

        var served = Assert.Single(report.Results, r => r.Dependency.Id == "renderer.served");
        Assert.Equal(ArtifactState.Unacquirable, served.State);
        Assert.Contains("build_renderer_model.py", served.Action!, StringComparison.Ordinal);
        Assert.DoesNotContain("renderer-shadow", ollama.Pulled);
    }

    // ---- modes -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DryRunDownloadsNothing()
    {
        var ollama = new FakeOllama(installed: true, serving: []);
        var downloader = new FakeDownloader();
        var lfs = new FakeLfs(installed: true);

        await Run(ollama, Discover(), BootstrapMode.DryRun, downloader: downloader, lfs: lfs);

        Assert.Empty(ollama.Pulled);
        Assert.Empty(downloader.Requested);
        Assert.Empty(lfs.Pulled);
    }

    [Fact]
    public async Task VerifyOnlyDownloadsNothingAndFailsWhenSomethingIsMissing()
    {
        var ollama = new FakeOllama(installed: true, serving: []);
        var report = await Run(ollama, Discover(), BootstrapMode.VerifyOnly);

        Assert.Empty(ollama.Pulled);
        Assert.False(report.Ok);
    }

    [Fact]
    public async Task ForceReacquiresOnlyTheNamedArtifact()
    {
        var ollama = new FakeOllama(installed: true, serving: ["chat-model", "embed-model"]);

        await Run(ollama, Discover(), BootstrapMode.Normal,
            force: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "model.conversation" });

        // Everything else was already present and stayed untouched.
        Assert.Equal(["chat-model"], ollama.Pulled);
    }

    // ---- secrets ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DiagnosticsNeverEchoACredentialBearingUri()
    {
        var secretUri = "https://user:hunter2@internal.example/weights.onnx?token=SECRETVALUE";
        var report = await Run(
            new FakeOllama(true, []),
            [Adapter(sha: null) with
            {
                ExpectedPath = "/repo/missing.onnx",
                Source = new ArtifactSource { Uri = secretUri },
            }],
            BootstrapMode.VerifyOnly);

        var printed = string.Join("\n", report.Results.Select(r => $"{r.Detail}\n{r.Action}"));
        Assert.DoesNotContain("hunter2", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETVALUE", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(secretUri, printed, StringComparison.Ordinal);
    }

    // ---- renderer routing ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, "", "DISABLED")]
    [InlineData(true, "", "SHADOW ONLY")]
    [InlineData(true, "usr-1", "CANARY")]
    public void RendererRoutingIsReportedAsThreeDistinctStates(bool enabled, string canary, string expected)
    {
        var described = ModelDependencies.DescribeRendererRouting(
            new RendererShadowOptions { Enabled = enabled, CanaryUserId = canary });

        Assert.StartsWith(expected, described, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdapterIsAFirstClassDependencyWithItsPinnedHashAndRequiredFiles()
    {
        var companion = new CompanionOptions();
        companion.RendererShadow.Enabled = true;
        companion.RendererShadow.AdapterSha256 = Sha;

        var deps = ModelDependencies.Discover(
            new ModelOptions(), new CognitiveModelOptions(), companion, new SafetyOptions(),
            "/models", "/repo");

        var adapter = Assert.Single(deps, d => d.Id == "renderer.adapter");
        Assert.True(adapter.Active);
        Assert.Equal(Sha, adapter.Sha256);
        Assert.NotEmpty(adapter.CompanionFiles);
        Assert.Contains(adapter.CompanionFiles, f => f.EndsWith("adapter_config.json", StringComparison.Ordinal));

        // Its base model is named and pinned, but is not a startup requirement.
        var baseModel = Assert.Single(deps, d => d.Id == "renderer.base");
        Assert.False(baseModel.Active);
        Assert.Equal("Qwen/Qwen2.5-3B-Instruct", baseModel.Source!.Repository);
        Assert.NotNull(baseModel.Source.Revision);
    }

    [Fact]
    public async Task AMissingAdapterFileBesideTheWeightsIsInvalid()
    {
        var files = new FakeFiles();
        files.AddFile("/repo/adapter.safetensors", RealWeights);

        var report = await Run(
            new FakeOllama(true, []),
            [Adapter(sha: Sha) with { CompanionFiles = ["/repo/adapter_config.json"] }],
            BootstrapMode.VerifyOnly, files: files);

        Assert.Equal(ArtifactState.Invalid, report.Results[0].State);
        Assert.Contains("adapter_config.json", report.Results[0].Detail, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Fixed bytes standing in for weights, and the pin that MATCHES them.</summary>
    private static readonly byte[] RealWeights = "pretend these are 120MB of tensors"u8.ToArray();

    private static string ShaOf(byte[] content)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static IReadOnlyList<ModelDependency> Discover()
    {
        var models = new ModelOptions
        {
            Provider = "OpenAiCompatible",
            Chat = new EndpointOptions { Model = "chat-model" },
            Embeddings = new EndpointOptions { Model = "embed-model" },
        };
        var companion = new CompanionOptions();
        companion.RendererShadow.Enabled = true;
        return ModelDependencies.Discover(
            models, Cognitive(), companion, new SafetyOptions(), "/models", "/repo");
    }

    /// <summary>
    /// Cognitive entries only become dependencies once a Path is configured — an entry naming no
    /// file is nothing to acquire. The shipped appsettings names all four, so the fixture does too.
    /// </summary>
    private static CognitiveModelOptions Cognitive() => new()
    {
        Reranker = new CognitiveModelEntry { Path = "reranker.onnx" },
        Nli = new CognitiveModelEntry { Path = "nli.onnx" },
        Classifier = new CognitiveModelEntry { Path = "classifier.onnx" },
        Emotion = new CognitiveModelEntry { Path = "emotion.onnx" },
    };

    private static ModelDependency Adapter(string? sha) => new()
    {
        Id = "renderer.adapter",
        Role = "renderer/adapter",
        Kind = DependencyKind.LocalFile,
        Identifier = "adapter.safetensors",
        Provider = "git-lfs",
        ExpectedPath = "/repo/adapter.safetensors",
        Active = true,
        Sha256 = sha,
    };

    private static Task<BootstrapReport> Run(
        FakeOllama ollama, IReadOnlyList<ModelDependency> deps, BootstrapMode mode,
        bool allConfigured = false, IReadOnlySet<string>? force = null,
        FakeFiles? files = null, FakeDownloader? downloader = null, FakeLfs? lfs = null)
        => new ModelBootstrap(ollama, downloader ?? new FakeDownloader(), lfs ?? new FakeLfs(true), files ?? new FakeFiles())
            .RunAsync(deps, mode, "test", allConfigured, force);

    // ---- fakes ------------------------------------------------------------------------------------------------

    private sealed class FakeOllama(bool installed, string[] serving, bool pullSucceeds = true) : IOllamaClient
    {
        private readonly HashSet<string> _serving = new(serving, StringComparer.OrdinalIgnoreCase);
        public List<string> Pulled { get; } = [];
        public bool IsInstalled => installed;

        public Task<IReadOnlySet<string>?> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>?>(installed ? _serving : null);

        public Task<(bool Ok, string Detail)> PullAsync(string tag, CancellationToken ct = default)
        {
            Pulled.Add(tag);
            if (!pullSucceeds)
                return Task.FromResult((false, "no such model"));
            _serving.Add(tag);
            return Task.FromResult((true, "ok"));
        }
    }

    private sealed class FakeDownloader(bool available = true, string? reason = null) : IArtifactDownloader
    {
        public List<string> Requested { get; } = [];
        public bool IsAvailable => available;
        public string? UnavailableReason => reason;

        public Task<(bool Ok, string Detail)> DownloadAsync(
            ArtifactSource source, string destination, CancellationToken ct = default)
        {
            Requested.Add(source.Repository ?? source.Uri ?? "?");
            return Task.FromResult((true, "fake"));
        }
    }

    private sealed class FakeLfs(bool installed, Action? onPull = null) : IGitLfsClient
    {
        public List<string> Pulled { get; } = [];
        public bool IsInstalled => installed;

        public Task<(bool Ok, string Detail)> PullAsync(string path, CancellationToken ct = default)
        {
            Pulled.Add(path);
            onPull?.Invoke();
            return Task.FromResult((true, "restored"));
        }
    }

    /// <summary>An in-memory disk. Never touches the real filesystem.</summary>
    private sealed class FakeFiles : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, byte[] content) => _files[path] = content;

        public bool FileExists(string path) => _files.ContainsKey(path);
        public bool DirectoryExists(string path) => _files.Keys.Any(k => k.StartsWith(path, StringComparison.OrdinalIgnoreCase));
        public Stream OpenRead(string path) => new MemoryStream(_files[path]);

        public Task<string> ReadHeadAsync(string path, int bytes, CancellationToken ct = default)
        {
            var content = _files[path];
            return Task.FromResult(Encoding.UTF8.GetString(content, 0, Math.Min(bytes, content.Length)));
        }
    }
}
