using Companion.Core;

namespace Companion.Infrastructure.Models;

/// <summary>How an artifact is obtained. Determines which acquirer can satisfy it.</summary>
public enum DependencyKind
{
    /// <summary>A tag served by an Ollama/OpenAI-compatible server: `ollama pull &lt;tag&gt;`.</summary>
    OllamaModel,

    /// <summary>A file on disk (ONNX weights, a LoRA adapter, a tokenizer vocabulary).</summary>
    LocalFile,

    /// <summary>A Hugging Face snapshot pinned to a revision.</summary>
    HuggingFaceSnapshot,

    /// <summary>
    /// An Ollama model that is BUILT locally from other artifacts rather than pulled. Nothing can
    /// download it; the bootstrap can only say what is missing and which script produces it.
    /// </summary>
    LocallyBuiltOllamaModel,
}

/// <summary>
/// One thing the effective configuration says must exist before Ava can run as configured.
/// </summary>
public sealed record ModelDependency
{
    /// <summary>Stable id for selecting a single dependency (-Force, reporting).</summary>
    public required string Id { get; init; }

    /// <summary>The role it serves, in the application's own vocabulary.</summary>
    public required string Role { get; init; }

    public required DependencyKind Kind { get; init; }

    /// <summary>The configured identifier exactly as the application uses it. Never normalized.</summary>
    public required string Identifier { get; init; }

    /// <summary>Provider name as configured ("OpenAiCompatible", "local-file", "huggingface").</summary>
    public required string Provider { get; init; }

    /// <summary>Server base URL for provider-served models; null for files.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Where the artifact is expected on this machine, when that is knowable.</summary>
    public string? ExpectedPath { get; init; }

    /// <summary>
    /// Whether the effective configuration actually needs this now. False means the capability
    /// that would use it is switched off — normal startup must not download it.
    /// </summary>
    public required bool Active { get; init; }

    /// <summary>Why it is inactive, in the configuration's own terms. Null when active.</summary>
    public string? InactiveReason { get; init; }

    /// <summary>
    /// A pinned SHA-256 when configuration records one. Its ABSENCE is reported rather than
    /// silently treated as "verified" — an unpinned artifact can only get provider-level checks.
    /// </summary>
    public string? Sha256 { get; init; }

    public ArtifactSource? Source { get; init; }

    /// <summary>Additional files that must exist beside <see cref="ExpectedPath"/>.</summary>
    public IReadOnlyList<string> CompanionFiles { get; init; } = [];
}

/// <summary>
/// The one derivation of "what does this configuration require?".
///
/// Everything here reads the application's own typed options — no second hand-maintained roster
/// that could drift from what the app actually loads. <see cref="ModelPreflight"/> consumes the
/// language-model half of this same list, so a role added to the app is a role the bootstrap
/// sees, automatically.
/// </summary>
public static class ModelDependencies
{
    /// <summary>
    /// The language-model roles, resolved through the same fallback chain the application uses
    /// (Extraction falls back to Chat, Reranker to Summarizer, and so on). Roles the provider
    /// serves; the audio endpoints are a separate optional server and are handled below.
    /// </summary>
    public static IReadOnlyList<(string Role, EndpointOptions Endpoint)> ProviderRoles(ModelOptions models)
    {
        var roles = new List<(string, EndpointOptions)>
        {
            (ProviderHttpClients.Conversation, models.Chat),
            (ProviderHttpClients.Extraction, models.ExtractionOrChat),
            (ProviderHttpClients.Summarizer, models.SummarizerOrChat),
            (ProviderHttpClients.Reranker, models.RerankerOrSummarizer),
            (ProviderHttpClients.Safety, models.SafetyOrExtraction),
            (ProviderHttpClients.TaskAuditor, models.TaskAuditorOrSummarizer),
            (ProviderHttpClients.ToolPlanner, models.ToolPlannerOrExtraction),
            (ProviderHttpClients.Embeddings, models.Embeddings),
        };
        if (models.Vision is { } vision)
            roles.Add((ProviderHttpClients.Vision, vision));
        return roles;
    }

    /// <summary>
    /// Everything the effective configuration names, active and inactive alike. The caller
    /// filters by <see cref="ModelDependency.Active"/>; nothing here decides what to download.
    /// </summary>
    public static IReadOnlyList<ModelDependency> Discover(
        ModelOptions models,
        CognitiveModelOptions cognitive,
        CompanionOptions companion,
        SafetyOptions safety,
        string cognitiveDirectory,
        string repositoryRoot)
    {
        var all = new List<ModelDependency>();
        var usesRealModel = models.UsesRealModel;

        // ---- provider-served language models -------------------------------------------------
        // The safety ROLE is only exercised when the safety gate is on; every other role is on
        // the ordinary turn path. Duplicate tags are kept as separate roles deliberately: the
        // report should say which roles share a model, and the acquirer dedupes by identifier.
        foreach (var (role, endpoint) in ProviderRoles(models))
        {
            var roleActive = usesRealModel
                && (role != ProviderHttpClients.Safety || safety.Enabled);
            all.Add(new ModelDependency
            {
                Id = $"model.{role}",
                Role = role,
                Kind = DependencyKind.OllamaModel,
                Identifier = endpoint.Model,
                Provider = models.Provider,
                BaseUrl = endpoint.BaseUrl,
                Active = roleActive && !string.IsNullOrWhiteSpace(endpoint.Model),
                InactiveReason =
                    !usesRealModel ? $"Models:Provider is {models.Provider} (no real model is called)"
                    : string.IsNullOrWhiteSpace(endpoint.Model) ? "no model configured for this role"
                    : role == ProviderHttpClients.Safety && !safety.Enabled ? "Safety:Enabled is false"
                    : null,
            });
        }

        // The audio endpoints are a separate server. Named so the inventory is complete, never
        // pulled: they are not Ollama tags and their absence is already reported honestly.
        foreach (var (role, endpoint) in new (string, EndpointOptions?)[]
                 {
                     (ProviderHttpClients.Transcription, models.Transcription),
                     (ProviderHttpClients.Speech, models.Speech),
                 })
        {
            if (endpoint is null)
                continue;
            all.Add(new ModelDependency
            {
                Id = $"model.{role}",
                Role = role,
                Kind = DependencyKind.HuggingFaceSnapshot,
                Identifier = endpoint.Model,
                Provider = "audio-server",
                BaseUrl = endpoint.BaseUrl,
                Active = false,
                InactiveReason = "served by a separate optional audio server, not acquired here",
            });
        }

        // ---- specialist ONNX models ----------------------------------------------------------
        foreach (var (name, entry) in new (string, CognitiveModelEntry)[]
                 {
                     ("reranker", cognitive.Reranker),
                     ("nli", cognitive.Nli),
                     ("classifier", cognitive.Classifier),
                     ("emotion", cognitive.Emotion),
                 })
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;
            var path = Path.IsPathRooted(entry.Path)
                ? entry.Path
                : Path.Combine(cognitiveDirectory, entry.Path);
            var vocab = entry.VocabPath ?? "vocab.txt";
            all.Add(new ModelDependency
            {
                Id = $"cognitive.{name}",
                Role = $"cognitive/{name}",
                Kind = DependencyKind.LocalFile,
                Identifier = entry.Path,
                Provider = "local-file",
                ExpectedPath = path,
                Active = entry.Enabled,
                InactiveReason = entry.Enabled ? null : $"CognitiveModels:{Capitalize(name)}:Enabled is false",
                Sha256 = string.IsNullOrWhiteSpace(entry.Sha256) ? null : entry.Sha256,
                Source = entry.Source,
                CompanionFiles = Path.IsPathRooted(vocab)
                    ? [vocab]
                    : [Path.Combine(Path.GetDirectoryName(path) ?? cognitiveDirectory, vocab)],
            });
        }

        // ---- the mouth: adapter, its base model, and the served model ------------------------
        // First-class because it is the only dependency with a pinned hash in configuration, and
        // because "is she using it, shadowing it, or is it off?" is a question with three answers.
        var shadow = companion.RendererShadow;
        var adapterPath = Path.IsPathRooted(shadow.AdapterPath)
            ? shadow.AdapterPath
            : Path.Combine(repositoryRoot, shadow.AdapterPath);

        all.Add(new ModelDependency
        {
            Id = "renderer.adapter",
            Role = "renderer/adapter (run-1c)",
            Kind = DependencyKind.LocalFile,
            Identifier = shadow.AdapterPath,
            Provider = "git-lfs",
            ExpectedPath = adapterPath,
            Active = shadow.Enabled,
            InactiveReason = shadow.Enabled ? null : "Companion:RendererShadow:Enabled is false",
            // The configured AdapterSha256 IS the pin, and it is also the Git LFS object id.
            Sha256 = string.IsNullOrWhiteSpace(shadow.AdapterSha256) ? null : shadow.AdapterSha256,
            CompanionFiles = shadow.AdapterFiles
                .Select(f => Path.Combine(Path.GetDirectoryName(adapterPath) ?? repositoryRoot, f))
                .ToList(),
        });

        all.Add(new ModelDependency
        {
            Id = "renderer.base",
            Role = "renderer/base model",
            Kind = DependencyKind.HuggingFaceSnapshot,
            Identifier = shadow.BaseModel?.Repository ?? "",
            Provider = "huggingface",
            ExpectedPath = shadow.BaseModelPath is { Length: > 0 } p
                ? (Path.IsPathRooted(p) ? p : Path.Combine(repositoryRoot, p))
                : null,
            // The base is needed to BUILD or serve the adapter, not to run the app: the served
            // Ollama model is already merged. Naming it keeps the inventory honest.
            Active = false,
            InactiveReason = "needed only to rebuild or serve the adapter, not to run the app",
            Source = shadow.BaseModel,
        });

        // ---- the mouth: run-2 -----------------------------------------------------------------
        // Named even when disabled, so a fresh machine is told what it would need rather than
        // discovering it at the moment a turn wants it.
        var mouth = shadow.Mouth;
        var mouthAdapterPath = Path.IsPathRooted(mouth.AdapterPath)
            ? mouth.AdapterPath
            : Path.Combine(repositoryRoot, mouth.AdapterPath);

        all.Add(new ModelDependency
        {
            Id = "mouth.adapter",
            Role = "mouth/adapter (run-2)",
            Kind = DependencyKind.LocalFile,
            Identifier = mouth.AdapterPath,
            Provider = "git-lfs",
            ExpectedPath = mouthAdapterPath,
            Active = mouth.Enabled,
            InactiveReason = mouth.Enabled ? null : "Companion:RendererShadow:Mouth:Enabled is false",
            // The configured hash IS the pin and also the Git LFS object id, so a pointer left
            // unfetched fails the content check rather than passing an existence check.
            Sha256 = string.IsNullOrWhiteSpace(mouth.AdapterSha256) ? null : mouth.AdapterSha256,
            CompanionFiles = mouth.AdapterFiles
                .Select(f => Path.Combine(Path.GetDirectoryName(mouthAdapterPath) ?? repositoryRoot, f))
                .ToList(),
        });

        all.Add(new ModelDependency
        {
            Id = "mouth.base",
            Role = "mouth/base model",
            Kind = DependencyKind.HuggingFaceSnapshot,
            Identifier = mouth.BaseModel?.Repository ?? "",
            Provider = "huggingface",
            ExpectedPath = mouth.BaseModelPath is { Length: > 0 } mp
                ? (Path.IsPathRooted(mp) ? mp : Path.Combine(repositoryRoot, mp))
                : null,
            // Unlike run-1c's merged Ollama model, run-2 is served as base + adapter, so the base
            // is required to answer a turn rather than only to rebuild one.
            Active = mouth.Enabled,
            InactiveReason = mouth.Enabled ? null : "Companion:RendererShadow:Mouth:Enabled is false",
            Source = mouth.BaseModel,
        });

        all.Add(new ModelDependency
        {
            Id = "mouth.served",
            Role = "mouth/served endpoint",
            Kind = DependencyKind.LocallyBuiltOllamaModel,
            Identifier = "run-2",
            Provider = "serve_run2.py",
            BaseUrl = mouth.Endpoint,
            Active = mouth.Enabled,
            InactiveReason = mouth.Enabled ? null : "Companion:RendererShadow:Mouth:Enabled is false",
            Source = new ArtifactSource { BuiltBy = "training/mouth/serve_run2.py" },
        });

        all.Add(new ModelDependency
        {
            Id = "renderer.served",
            Role = "renderer/served model",
            Kind = DependencyKind.LocallyBuiltOllamaModel,
            Identifier = shadow.OllamaModel,
            Provider = "Ollama",
            BaseUrl = shadow.Endpoint,
            Active = shadow.Enabled,
            InactiveReason = shadow.Enabled ? null : "Companion:RendererShadow:Enabled is false",
            Source = new ArtifactSource { BuiltBy = "tools/build_renderer_model.py" },
        });

        return all;
    }

    /// <summary>
    /// How the renderer is wired right now, in one sentence. Three distinct states, because
    /// "enabled" and "displayed to a user" are different decisions.
    /// </summary>
    public static string DescribeRendererRouting(RendererShadowOptions shadow)
        => !shadow.Enabled
            ? "DISABLED - Companion:RendererShadow:Enabled is false; the adapter is never loaded or called."
            : string.IsNullOrWhiteSpace(shadow.CanaryUserId)
                ? "SHADOW ONLY - observed beside every eligible turn; no user is shown its output."
                : "CANARY - one configured user id sees its replies, with production as immediate fallback.";

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
