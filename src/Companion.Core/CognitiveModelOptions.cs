namespace Companion.Core;

/// <summary>
/// Where the specialist models live and which of them are switched on.
///
/// Every one is off by default and every one is optional. A companion with no models present must
/// behave exactly as it did before they existed — the heuristics they improve on are all still
/// there, and a missing file is an ordinary state rather than a failure.
/// </summary>
public sealed class CognitiveModelOptions
{
    public const string Section = "CognitiveModels";

    /// <summary>
    /// Where model files are, absolute or relative to the database. Kept next to the database
    /// rather than the binaries because that is the directory a person backs up and moves between
    /// machines, and the local roster differs per machine exactly as the Ollama one does.
    /// </summary>
    public string Directory { get; set; } = "models";

    /// <summary>Cross-encoder for relatedness: reranking, entity resolution, reference targeting.</summary>
    public CognitiveModelEntry Reranker { get; set; } = new();

    /// <summary>Entailment/contradiction, for supersession and the assertion veto.</summary>
    public CognitiveModelEntry Nli { get; set; } = new() { Threshold = 0.6 };

    /// <summary>Multi-label cognitive signals: decision, commitment, correction, unfinished work.</summary>
    public CognitiveModelEntry Classifier { get; set; } = new() { Threshold = 0.5 };

    /// <summary>Multi-label emotion, feeding the existing affect system.</summary>
    public CognitiveModelEntry Emotion { get; set; } = new() { Threshold = 0.3 };

    /// <summary>
    /// How long any single inference may take before the caller gives up and falls back. Generous
    /// for a 70M encoder on a CPU (single-digit milliseconds is normal) — this is a stuck-process
    /// guard, not a budget.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Run new models alongside the heuristics they would replace and record where they disagree,
    /// without letting them affect the answer. This is how a model earns its way in.
    /// </summary>
    public bool ShadowMode { get; set; }

    public IEnumerable<(string Name, CognitiveModelEntry Entry)> All()
    {
        yield return ("reranker", Reranker);
        yield return ("nli", Nli);
        yield return ("classifier", Classifier);
        yield return ("emotion", Emotion);
    }
}

/// <summary>One specialist model's configuration.</summary>
public sealed class CognitiveModelEntry
{
    /// <summary>Off unless asked for. An absent model is never a reason to fail.</summary>
    public bool Enabled { get; set; }

    /// <summary>Filename within <see cref="CognitiveModelOptions.Directory"/>, or an absolute path.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The tokenizer vocabulary beside the model. Defaults to <c>vocab.txt</c> next to it, which is
    /// what a Hugging Face export produces.
    /// </summary>
    public string? VocabPath { get; set; }

    /// <summary>Below this the model's answer is treated as "no opinion" and the caller falls back.</summary>
    public double Threshold { get; set; }

    /// <summary>
    /// When true, being unable to load this model fails startup instead of falling back. Off by
    /// default: the point of specialist models here is that they are an improvement on something
    /// that already works. Turn it on when you would rather find out immediately than quietly get
    /// the old behaviour back.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>Longest input in tokens; anything beyond is truncated before inference.</summary>
    public int MaxTokens { get; set; } = 256;
}
