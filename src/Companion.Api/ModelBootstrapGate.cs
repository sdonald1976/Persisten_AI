using Companion.Core;
using Companion.Infrastructure.Models;
using Companion.Infrastructure.Models.Bootstrap;

namespace Companion.Api;

/// <summary>
/// Pulls the models this configuration needs, before the API serves anything.
///
/// The failure this exists to end: a second machine with an empty Ollama came up, found Stheno
/// present and everything else missing, and talked normally while remembering nothing. Silently
/// degraded is the worst shape a companion can fail in — she answers, so nothing looks wrong, and
/// the extraction and embedding calls 404 behind her.
///
/// <c>ModelPreflightWorker</c> already DETECTED exactly that and logged it. What it could not do
/// was act, and a log line on a minimised console window is not a control. This runs the same
/// <see cref="ModelBootstrap"/> the CLI tool runs — one implementation, not two — and refuses to
/// start when a required model cannot be acquired.
///
/// Scope is deliberately narrow: <b>Ollama tags only</b>. Those are the things a service can
/// honestly fetch at boot. It never restores Git LFS objects, never downloads a Hugging Face
/// snapshot, and never blocks on the locally-built renderer, whose absence the shadow path
/// already handles by falling back silently. Widening this would mean a published deployment
/// refusing to start because a training adapter it never loads is not on disk.
/// </summary>
internal static class ModelBootstrapGate
{
    /// <summary>Returns false when a required model is unavailable and the app must not serve.</summary>
    public static async Task<bool> RunAsync(
        IConfiguration configuration, string contentRoot, ILogger logger,
        CancellationToken ct = default)
    {
        var models = configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>()
                     ?? new ModelOptions();

        // Mock provider calls no real model, so there is nothing to acquire and nothing to fail on.
        if (!models.UsesRealModel)
            return true;

        if (!models.AutoPull)
        {
            logger.LogDebug(
                "Model auto-pull is off (Models:AutoPull=false); missing models will be reported by "
                + "preflight but not acquired.");
            return true;
        }

        var cognitive = configuration.GetSection(CognitiveModelOptions.Section).Get<CognitiveModelOptions>()
                        ?? new CognitiveModelOptions();
        var companion = configuration.GetSection(CompanionOptions.SectionName).Get<CompanionOptions>()
                        ?? new CompanionOptions();
        var safety = configuration.GetSection(SafetyOptions.Section).Get<SafetyOptions>()
                     ?? new SafetyOptions();

        var dependencies = ModelDependencies
            .Discover(models, cognitive, companion, safety, contentRoot, contentRoot)
            // Active, and only the kind a running service can legitimately fetch.
            .Where(d => d.Active && d.Kind == DependencyKind.OllamaModel)
            .ToList();

        if (dependencies.Count == 0)
            return true;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var bootstrap = new ModelBootstrap(
            new OllamaCliClient(http, models.Chat.BaseUrl),
            new HuggingFaceDownloader(),
            new GitLfsCliClient(contentRoot),
            new LocalFileSystem());

        var report = await bootstrap.RunAsync(
            dependencies, BootstrapMode.Normal,
            ModelDependencies.DescribeRendererRouting(companion.RendererShadow), ct: ct);

        foreach (var problem in report.PrerequisiteProblems)
            logger.LogError("Model bootstrap: {Problem}", problem);

        foreach (var acquired in report.Results.Where(r => r.Acquired))
            logger.LogInformation(
                "Pulled {Model} for {Role} before startup.",
                acquired.Dependency.Identifier, acquired.Dependency.Role);

        if (report.Ok)
            return true;

        foreach (var failure in report.Results.Where(r => r.Dependency.Active && r.IsFailure))
            logger.LogError(
                "Required model {Model} ({Role}) is unavailable: {Detail}{Action}",
                failure.Dependency.Identifier, failure.Dependency.Role, failure.Detail,
                failure.Action is { } action ? $" -> {action}" : "");

        logger.LogError(
            "Refusing to start: a required model could not be acquired. Nothing was substituted. "
            + "Fix the above, or set Models:AutoPull=false to start anyway and accept the "
            + "degraded behaviour those roles will produce.");
        return false;
    }
}
