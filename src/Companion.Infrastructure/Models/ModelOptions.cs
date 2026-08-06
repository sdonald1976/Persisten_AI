namespace Companion.Infrastructure.Models;

/// <summary>
/// Selects and configures the language-model providers. Bound from the "Models" section of
/// configuration. The default provider is "Mock" (fully offline, deterministic); set it to
/// "OpenAiCompatible" (or "Ollama"/"LMStudio") to use a real local server.
/// </summary>
public sealed class ModelOptions
{
    public const string SectionName = "Models";

    /// <summary>"Mock" for the offline stand-ins, anything else for the OpenAI-compatible server.</summary>
    public string Provider { get; set; } = "Mock";

    /// <summary>The conversational model (larger, better quality). Used for the assistant's reply.</summary>
    public EndpointOptions Chat { get; set; } = new();

    /// <summary>Model for memory extraction (smaller, structured-output-friendly). Falls back to <see cref="Chat"/>.</summary>
    public EndpointOptions? Extraction { get; set; }

    /// <summary>Model for consolidation summaries (cheap and fast). Falls back to <see cref="Chat"/>.</summary>
    public EndpointOptions? Summarizer { get; set; }

    /// <summary>Dedicated embedding model.</summary>
    public EndpointOptions Embeddings { get; set; } = new();

    /// <summary>Optional multimodal model for images (e.g. llama3.2-vision, llava). Enables the <c>/image</c> command.</summary>
    public EndpointOptions? Vision { get; set; }

    /// <summary>
    /// Optional speech-to-text (Whisper) endpoint. Requires a separate audio server — Ollama and
    /// LM Studio don't do audio. Enables the <c>/transcribe</c> command.
    /// </summary>
    public EndpointOptions? Transcription { get; set; }

    public bool UsesRealModel =>
        !string.Equals(Provider, "Mock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extraction endpoint, or the conversational one if not separately configured.</summary>
    public EndpointOptions ExtractionOrChat => Extraction ?? Chat;

    /// <summary>Summarizer endpoint, or the conversational one if not separately configured.</summary>
    public EndpointOptions SummarizerOrChat => Summarizer ?? Chat;
}

/// <summary>Connection details for one OpenAI-compatible endpoint (chat or embeddings).</summary>
public sealed class EndpointOptions
{
    /// <summary>Base URL including the version segment, e.g. Ollama "http://localhost:11434/v1"
    /// or LM Studio "http://localhost:1234/v1".</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>The model name/tag as the server knows it, e.g. "dolphin-llama3" or "nomic-embed-text".</summary>
    public string Model { get; set; } = "";

    /// <summary>Optional bearer token. Local servers usually ignore it; LM Studio accepts any value.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Request timeout; local models can be slow on first load.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Informational only — the true dimension is whatever the model returns.</summary>
    public int Dimensions { get; set; } = 0;

    /// <summary>
    /// Sampling temperature (0 = deterministic, higher = more creative). Lower values reduce the
    /// random free-association small models sometimes produce. Null = use the server's default.
    /// Applies to chat/vision endpoints; ignored for embeddings.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>Optional cap on generated tokens. Null = use the server's default.</summary>
    public int? MaxTokens { get; set; }
}
