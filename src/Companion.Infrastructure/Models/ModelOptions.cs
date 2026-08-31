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

    /// <summary>
    /// Pull missing Ollama models before the API serves anything, and refuse to start if one
    /// cannot be acquired.
    ///
    /// On by default because this is single-user local software and the alternative is worse: a
    /// machine with an empty Ollama comes up, finds the conversational model present and the rest
    /// missing, and talks normally while remembering nothing. Slow first boot beats silently
    /// amnesiac.
    ///
    /// Turn it off on a box where an unattended multi-gigabyte download would be unwelcome; the
    /// preflight worker still reports what is missing.
    /// </summary>
    public bool AutoPull { get; set; } = true;

    /// <summary>The conversational model (larger, better quality). Used for the assistant's reply.</summary>
    public EndpointOptions Chat { get; set; } = new();

    /// <summary>Model for memory extraction (smaller, structured-output-friendly). Falls back to <see cref="Chat"/>.</summary>
    public EndpointOptions? Extraction { get; set; }

    /// <summary>Model for consolidation summaries (cheap and fast). Falls back to <see cref="Chat"/>.</summary>
    public EndpointOptions? Summarizer { get; set; }

    /// <summary>Optional memory reranker. Falls back to <see cref="Summarizer"/> then <see cref="Chat"/>.</summary>
    public EndpointOptions? Reranker { get; set; }

    /// <summary>Optional safety/privacy classifier. Falls back to <see cref="Extraction"/> then <see cref="Chat"/>.</summary>
    public EndpointOptions? Safety { get; set; }

    /// <summary>Optional task-completion auditor. Falls back to <see cref="Summarizer"/> then <see cref="Chat"/>.</summary>
    public EndpointOptions? TaskAuditor { get; set; }

    /// <summary>
    /// Optional executive tool planner: decides what the companion should look up before the
    /// conversational model replies. Best served by a small instruction-following model (the
    /// conversational RP model is exactly what it should NOT be). Falls back to
    /// <see cref="Extraction"/> (already the structured-output role), then <see cref="Chat"/>.
    /// </summary>
    public EndpointOptions? ToolPlanner { get; set; }

    /// <summary>
    /// Optional executive planner for the Stheno-free route: consumes the typed native plan/4
    /// and proposes a bounded refinement, never a word of the reply. No fallback chain on
    /// purpose - unset means the deterministic plan is used as-is, and quietly borrowing the
    /// conversational model for the planning seat would put Stheno back into a route whose
    /// contract is its absence.
    /// </summary>
    public EndpointOptions? ExecutivePlanner { get; set; }

    /// <summary>
    /// Model for post-turn reflection and the sleep cycle. Falls back to <see cref="Chat"/>,
    /// which is what reflection always used; configuring it moves the background off the
    /// conversational model entirely.
    /// </summary>
    public EndpointOptions? Reflection { get; set; }

    /// <summary>Dedicated embedding model.</summary>
    public EndpointOptions Embeddings { get; set; } = new();

    /// <summary>Optional multimodal model for images (e.g. llama3.2-vision, llava). Enables the <c>/image</c> command.</summary>
    public EndpointOptions? Vision { get; set; }

    /// <summary>
    /// Optional speech-to-text (Whisper) endpoint. Requires a separate audio server — Ollama and
    /// LM Studio don't do audio. Enables the <c>/transcribe</c> command.
    /// </summary>
    public EndpointOptions? Transcription { get; set; }

    /// <summary>
    /// Optional text-to-speech endpoint (OpenAI-compatible <c>/v1/audio/speech</c>: Piper via a
    /// wrapper, Speaches, LocalAI, …). Requires a separate audio server. Enables <c>POST /speak</c>,
    /// the output half of the voice loop.
    /// </summary>
    public EndpointOptions? Speech { get; set; }

    public bool UsesRealModel =>
        !string.Equals(Provider, "Mock", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extraction endpoint, or the conversational one if not separately configured.</summary>
    public EndpointOptions ExtractionOrChat => Extraction ?? Chat;

    /// <summary>Summarizer endpoint, or the conversational one if not separately configured.</summary>
    public EndpointOptions SummarizerOrChat => Summarizer ?? Chat;

    /// <summary>Reranker endpoint, or the cheap summarizer fallback.</summary>
    public EndpointOptions RerankerOrSummarizer => Reranker ?? SummarizerOrChat;

    /// <summary>Safety endpoint, or the structured extraction fallback.</summary>
    public EndpointOptions SafetyOrExtraction => Safety ?? ExtractionOrChat;

    /// <summary>Task auditor endpoint, or the cheap summarizer fallback.</summary>
    public EndpointOptions TaskAuditorOrSummarizer => TaskAuditor ?? SummarizerOrChat;

    /// <summary>Reflection endpoint, or the conversational one it historically used.</summary>
    public EndpointOptions ReflectionOrChat => Reflection ?? Chat;

    /// <summary>Tool planner endpoint, or the structured extraction fallback.</summary>
    public EndpointOptions ToolPlannerOrExtraction => ToolPlanner ?? ExtractionOrChat;
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
    /// Speech-only: the default voice name the TTS server should use (e.g. "alloy", or a Piper voice
    /// id). Null lets the server pick its default; a per-request voice can still override it.
    /// </summary>
    public string? Voice { get; set; }

    /// <summary>
    /// Speech-only: the audio format to request ("mp3", "wav", "opus", …). Null defaults to "mp3".
    /// Determines the response's content type.
    /// </summary>
    public string? AudioFormat { get; set; }

    /// <summary>
    /// Sampling temperature (0 = deterministic, higher = more creative). Lower values reduce the
    /// random free-association small models sometimes produce. Null = use the server's default.
    /// Applies to chat/vision endpoints; ignored for embeddings.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>Optional cap on generated tokens. Null = use the server's default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// The context window the server will actually give this model, in tokens. Used to size the
    /// prompt so it cannot overflow.
    ///
    /// It has to be stated rather than discovered, because nothing in the OpenAI-compatible
    /// protocol reports it and the obvious guesses are wrong. A model's advertised training
    /// length is not what it is served with: Stheno is an 8192-token model, and Ollama loads it
    /// at 4096 unless told otherwise. Nor can the client ask for more — <c>num_ctx</c> sent to
    /// Ollama's OpenAI-compatible endpoint is accepted and ignored, both nested under
    /// <c>options</c> and at the top level (verified against Ollama directly; the loaded context
    /// stayed at 4096 either way). Raising it means restarting the server with
    /// <c>OLLAMA_CONTEXT_LENGTH</c>, or a Modelfile.
    ///
    /// So the honest default is the small one. Overflow is not a soft failure: the server drops
    /// the excess from the top of the prompt and answers from what remains, which is how a
    /// companion comes to deny, fluently, that she was ever told anything.
    /// </summary>
    public int ContextTokens { get; set; } = 4096;

    /// <summary>
    /// Tokens held back from <see cref="ContextTokens"/> for the reply and for the gap between a
    /// 4-chars-per-token estimate and the model's real tokenizer. The estimate is fine on prose
    /// and optimistic on code, punctuation, and names, so the reserve absorbs being wrong in the
    /// direction that matters — running out of window mid-prompt rather than mid-sentence.
    /// </summary>
    public int ReplyReserveTokens { get; set; } = 1024;

    /// <summary>What is left for the prompt once the reply has its room. Never negative.</summary>
    public int PromptBudgetTokens =>
        ContextTokens <= 0 ? 0 : Math.Max(512, ContextTokens - Math.Max(0, ReplyReserveTokens));

    /// <summary>
    /// Sequences that end generation (OpenAI-compatible <c>stop</c>; honored by Ollama and LM Studio).
    ///
    /// The conversational endpoint gets defaults it would not otherwise need. Her context arrives as
    /// a structured document — headings, rules, labelled sections — and a roleplay fine-tune shown a
    /// document will sometimes *continue* it instead of answering, appending a plausible-looking
    /// section of its own. One real reply ended with a horizontal rule followed by "Remembered items
    /// about the user so far: - None (first conversation)", which appears nowhere in any prompt: she
    /// invented a packet-shaped section because the packet taught her the shape.
    ///
    /// Stopping on the shape is the only fix that acts before the text exists. Applied to
    /// conversation only — the structured roles return JSON and must never be cut short.
    /// </summary>
    public string[]? Stop { get; set; }

    /// <summary>
    /// Penalizes tokens by how often they've already appeared (OpenAI-compatible; honored by Ollama
    /// and LM Studio). The main lever against a model repeating itself — small local/abliterated
    /// models loop without it. Try ~0.3–0.8. Null = use the server's default.
    /// </summary>
    public double? FrequencyPenalty { get; set; }

    /// <summary>
    /// Penalizes tokens that have appeared at all, nudging the model toward new topics. Complements
    /// <see cref="FrequencyPenalty"/> against repetition. Try ~0.2–0.6. Null = server default.
    /// </summary>
    public double? PresencePenalty { get; set; }

    /// <summary>
    /// When the server reports the reply was cut off by the output-token limit
    /// (<c>finish_reason: "length"</c>), automatically ask the model to continue where it stopped —
    /// looping until it finishes naturally — so long tasks ("write me a story") complete in one
    /// turn instead of needing the user to say "keep going". Bounded by <see cref="MaxContinuations"/>.
    /// </summary>
    public bool AutoContinue { get; set; } = true;

    /// <summary>Hard cap on automatic continuation rounds per turn (runaway guard).</summary>
    public int MaxContinuations { get; set; } = 6;

    /// <summary>
    /// After a reply that stopped on its own (not cut off by the token limit), ask a small model
    /// whether it actually finished the task — the only signal that catches a model that
    /// self-truncates (writes a chunk of a story/plan and stops). Off by default: it's only as good
    /// as the small judge model, and an unreliable judge turns an already-finished reply into
    /// runaway continuation. Turn it on once your judge model is trusted (watch the logs). The
    /// safe, transport-level continuation (<c>finish_reason: "length"</c>) does not depend on this.
    /// Only consulted for replies at least <see cref="CompletionCheckMinChars"/> long; requires
    /// <see cref="AutoContinue"/>; applies to the conversational endpoint only.
    /// </summary>
    public bool CompletionCheck { get; set; }

    /// <summary>
    /// Minimum reply length (characters) before the semantic completion check runs. Short answers
    /// are self-evidently complete; only long-form output risks being cut off partway.
    /// </summary>
    public int CompletionCheckMinChars { get; set; } = 600;

    /// <summary>
    /// Log the full system prompt, user message, and raw reply for every call to this endpoint (at
    /// Information level) so you can see exactly what the model received and produced. Verbose and
    /// includes remembered context — off by default; turn on with the log level raised to see it.
    /// </summary>
    public bool LogPayloads { get; set; }

    /// <summary>
    /// Max transient-failure retries (429 / 5xx / connection blips / timeouts) with exponential
    /// backoff. 0 disables retries. Only ever applied to these stateless model calls.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Hard cap on a (non-streaming) response body, in bytes. A larger body is rejected rather
    /// than buffered, bounding memory against a hostile or malfunctioning endpoint.
    /// </summary>
    public long MaxResponseBytes { get; set; } = 8 * 1024 * 1024;
}
