namespace Companion.Core;

/// <summary>
/// Declarative acquisition metadata for a model artifact that is not simply an Ollama tag.
///
/// Deliberately explicit. A repository is never inferred from a filename: "classifier.onnx"
/// names no repository, and guessing one is how a bootstrap downloads plausible-looking wrong
/// weights and reports success. When this is absent the artifact is reported as unacquirable,
/// naming what configuration would need to say.
///
/// It lives beside the model options it describes rather than in a separate manifest, so adding
/// a model and stating where it comes from are the same edit.
/// </summary>
public sealed record ArtifactSource
{
    /// <summary>Hugging Face repository id, e.g. "Qwen/Qwen2.5-3B-Instruct".</summary>
    public string? Repository { get; init; }

    /// <summary>
    /// Exact revision (a commit sha). A branch name is not a pin — it moves, and then the
    /// artifact a second machine gets is not the artifact this one was measured against.
    /// </summary>
    public string? Revision { get; init; }

    /// <summary>A single file within the repository, when a whole snapshot is not wanted.</summary>
    public string? File { get; init; }

    /// <summary>A direct download URI, for artifacts not on Hugging Face.</summary>
    public string? Uri { get; init; }

    /// <summary>
    /// The script that produces this artifact when nothing can download it. Set for things that
    /// are BUILT locally, so the bootstrap can say what to run instead of only that it is absent.
    /// </summary>
    public string? BuiltBy { get; init; }

    /// <summary>Whether there is enough here to fetch the artifact without guessing.</summary>
    public bool CanAcquire => Repository is not null || Uri is not null;
}
