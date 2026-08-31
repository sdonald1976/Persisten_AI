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

    /// <summary>
    /// Let the cross-encoder actually reorder retrieved memories, rather than only being loaded and
    /// measured. Separate from <c>Reranker.Enabled</c> on purpose: loading a model so it can be
    /// judged in shadow is a different decision from putting it in the path of every turn, and
    /// conflating them is how a model gets promoted for being present.
    ///
    /// Off, because it has not earned it. On this project's resolution set the cross-encoder and
    /// the keyword score are level (11/12 each), so there is no measured reason to prefer it yet.
    /// </summary>
    public bool RerankMemories { get; set; }

    /// <summary>
    /// Run the cross-encoder AND the deterministic rule reranker in SHADOW beside the
    /// authoritative reranker on every eligible retrieval, recording all three orderings without
    /// affecting the displayed turn. Independent of <see cref="RerankMemories"/> (which decides
    /// which reranker is AUTHORITATIVE): shadow observes, promotion decides. Requires
    /// <see cref="Reranker"/> to be enabled so the cross-encoder can load. Off by default.
    /// </summary>
    public bool RerankShadow { get; set; }

    /// <summary>Where shadow reranker comparisons are appended (JSONL). Relative to the database
    /// directory, like the model directory, so the data travels with the deployment.</summary>
    public string RerankShadowPath { get; set; } = "rerank-shadow/shadow.jsonl";

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

    /// <summary>
    /// Record the heuristics' verdicts on real sentences, so the corpus a model is judged on stops
    /// being entirely synthetic. Off by default, and a separate switch from <see cref="ShadowMode"/>
    /// on purpose: shadow mode needs a model to compare against, capture needs only the rule, and
    /// capture is therefore the one that is useful when there is no model yet.
    ///
    /// It writes user text into the telemetry store, which the rest of that store deliberately
    /// avoids. Three things bound it: it runs only on turns already allowed to produce durable
    /// memory (so nothing private, in-character, or off the record is captured), the text is
    /// dropped and only the verdict kept when it looks like a credential, and it is off unless
    /// somebody sets this. Switching it on is a decision about your own conversations.
    /// </summary>
    public bool Capture { get; set; }

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

    /// <summary>
    /// SHA-256 of the file at <see cref="Path"/>, when it is pinned. Bootstrap verifies against
    /// this; its ABSENCE is reported rather than treated as verified, because "no pinned hash"
    /// and "hash matched" are different states and only one of them is evidence.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Where the file comes from, stated explicitly. A repository is never inferred from a
    /// filename: "classifier.onnx" names no repository, and guessing one is how a bootstrap
    /// downloads the wrong weights and calls it success.
    /// </summary>
    public ArtifactSource? Source { get; set; }

    /// <summary>Longest input in tokens; anything beyond is truncated before inference.</summary>
    public int MaxTokens { get; set; } = 256;
}
