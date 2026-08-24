namespace Companion.Core;

/// <summary>
/// Configuration-bound knobs for retrieval and context assembly. Bound from
/// the "Companion" section of appsettings.json.
/// </summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Companion";

    /// <summary>Max memories to include in the context packet after ranking.</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Approximate token budget for the memory section of the packet.</summary>
    public int MemoryTokenBudget { get; set; } = 800;

    /// <summary>
    /// Soft ceiling for the WHOLE rendered packet. Exceeding it logs a warning and shows up in
    /// turn diagnostics rather than silently truncating: on a small local model a bloated prompt
    /// degrades replies long before it errors, and quietly dropping context would trade a visible
    /// problem for an invisible one. Set to 0 to disable the warning.
    /// </summary>
    public int PacketTokenWarningThreshold { get; set; } = 3000;

    /// <summary>
    /// Hard ceiling for the rendered prompt, in tokens. Sections are dropped lowest-value first
    /// to stay under it; identity and the standing rules are never among them. Zero disables the
    /// limit, which should only ever be a deliberate choice for a model with room to spare.
    ///
    /// This is not the same idea as <see cref="PacketTokenWarningThreshold"/>, and the difference
    /// is the whole point. That one warns; this one acts. A warning is the right response to a
    /// prompt that is merely getting fat, but a prompt that exceeds the model's context window is
    /// not degraded, it is mutilated: the server discards the overflow from the top — identity,
    /// standing rules, the oldest turns — and answers from the remainder without reporting
    /// anything. The reply comes back fluent, confident, and severed from everything the
    /// companion is supposed to be.
    ///
    /// Set from the chat endpoint's context window at startup, minus room for the reply. Left at
    /// this default it fits the smallest window in common use, because being needlessly frugal
    /// costs a little nuance and guessing high costs her identity.
    /// </summary>
    public int PromptTokenBudget { get; set; } = 3000;

    /// <summary>How many recent messages to include verbatim.</summary>
    public int RecentMessageCount { get; set; } = 6;

    /// <summary>Recency half-life in days for the recency signal.</summary>
    public double RecencyHalfLifeDays { get; set; } = 45.0;

    /// <summary>Minimum combined score for a memory to be eligible for inclusion.</summary>
    public double MinScore { get; set; } = 0.05;

    /// <summary>
    /// Minimum topical relevance — raw semantic similarity + keyword overlap + project match — a
    /// memory must reach before it can enter the context packet. Recency, importance, and confidence
    /// rank the relevant memories but never admit one on their own; without this floor a recent or
    /// important fact scores above <see cref="MinScore"/> with zero relevance to the current turn,
    /// so unrelated things the companion "knows about the user" bleed into every reply. Also gates
    /// the open-loop boost (an unresolved item is only surfaced when the turn is already relevant to it).
    /// </summary>
    public double RelevanceFloor { get; set; } = 0.15;

    /// <summary>Per-signal weights for the hybrid retrieval score.</summary>
    public RetrievalWeights Weights { get; set; } = new();

    /// <summary>When true, each turn runs the extraction pipeline over the exchange.</summary>
    public bool EnableExtraction { get; set; } = true;

    /// <summary>Cosine similarity at/above which a candidate is treated as the same memory.</summary>
    public double DuplicateSimilarityThreshold { get; set; } = 0.82;

    /// <summary>Minimum final confidence for a brand-new memory to be accepted.</summary>
    public double MinAcceptConfidence { get; set; } = 0.35;

    /// <summary>
    /// Similarity at/above which a same-slot fact with a different value is treated as a
    /// change to the SAME topic (held for review) rather than an unrelated new fact. Below
    /// the duplicate threshold; above this, "user prefers X" vs "user prefers Y" only
    /// conflicts when X and Y are actually about the same thing.
    /// </summary>
    public double ContradictionSimilarityThreshold { get; set; } = 0.5;

    /// <summary>
    /// Similarity floor for replacing a fact the user has said they are changing ("actually, I've
    /// gone off black coffee"). This is a floor, not a test: the user's wording decides that a
    /// replacement is happening, and this only keeps it from landing on an unrelated memory.
    ///
    /// Measured on nomic-embed-text, which is why it sits at 0.6 — the true replacement is barely
    /// above facts that must NOT be touched, so nothing higher is safe and nothing lower is:
    ///   0.763 coffee black → oat milk lattes (must replace)   0.567 coffee vs coriander (must not)
    ///   0.753 coriander vs olives (must not)                  0.493 coffee vs irrigation (must not)
    /// See <see cref="Services.FactSupersession"/>.
    /// </summary>
    public double ReplacementSimilarityThreshold { get; set; } = 0.6;

    /// <summary>Minimum resolution score for a project to be considered a candidate at all.</summary>
    public double ResolutionMinScore { get; set; } = 0.15;

    /// <summary>
    /// Relative confidence (top / (top + runner-up)) the best candidate must reach to be
    /// picked without asking. Below it, with a viable runner-up, the resolver asks to clarify.
    /// </summary>
    public double ResolutionConfidenceThreshold { get; set; } = 0.65;

    /// <summary>How many relevant open loops to surface per turn.</summary>
    public int MaxOpenLoops { get; set; } = 3;

    /// <summary>Minimum number of related memories before they're consolidated (don't overgeneralize).</summary>
    public int ConsolidationMinObservations { get; set; } = 3;

    /// <summary>Similarity at/above which same-slot memories are considered the same topic for consolidation.</summary>
    public double ConsolidationMinSimilarity { get; set; } = 0.4;

    /// <summary>Master switch for the between-session reflection pass (the inner monologue).</summary>
    public bool EnableReflection { get; set; } = true;

    /// <summary>How long the user must have been quiet before a reflection pass may run.</summary>
    public int ReflectionIdleMinutes { get; set; } = 30;

    /// <summary>How often the background worker checks whether it's time to reflect.</summary>
    public int ReflectionCheckMinutes { get; set; } = 5;

    /// <summary>
    /// Minimum new user messages since the last watermark before a pass runs — a one-line visit
    /// isn't worth a thought, and the guard keeps quiet days from costing model calls.
    /// </summary>
    public int ReflectionMinNewMessages { get; set; } = 3;

    /// <summary>Cap on messages one pass reads (a long backlog keeps the freshest).</summary>
    public int ReflectionMaxMessages { get; set; } = 60;

    /// <summary>Max curiosities kept from one pass — wonder about a couple of things, not everything.</summary>
    public int ReflectionMaxCuriosities { get; set; } = 2;

    /// <summary>
    /// Minimum time between voicing two curiosities. One question, then let the conversation
    /// breathe — this is what keeps proactive curiosity from feeling like an interview.
    /// </summary>
    public double CuriosityCooldownHours { get; set; } = 1.0;

    /// <summary>
    /// Let the chat model intentionally invoke read-only tools (memory search, capability list,
    /// diagnostics, …) before replying. Adds up to MaxToolCallsPerTurn+1 model calls to a turn
    /// when the model chooses to look things up; offline mocks never produce a tool call.
    /// </summary>
    public bool EnableToolUse { get; set; } = true;

    /// <summary>
    /// The ONE promoted turn intent (language-organ Phase 2): when on, a turn the classifier
    /// selects as clarify — a question hanging on ambiguity the system could not resolve —
    /// puts one authoritative line in the packet preferring a short clarifying question over
    /// guessing. Off by default; every other intent stays shadow-only. Promoted on three live
    /// specimens where the system said clarify and the model answered anyway; the canonical
    /// soak stage measures whether this flag actually changes that.
    /// </summary>
    public bool PromoteClarifyIntent { get; set; }

    /// <summary>
    /// The knowledge-boundary promotion (docs/CONCEPT_KNOWLEDGE.md §3): when on, a direct
    /// epistemic question ("do you know what an axe is?") gets one authoritative packet
    /// line answering from Ava's concept store — she HAS learned it (with provenance) or
    /// has NOT (and must not present pretrained understanding as her own). Off by default:
    /// same promotion discipline as clarify, measured before trusted.
    /// </summary>
    public bool PromoteKnowledgeBoundary { get; set; }

    /// <summary>
    /// The narrowest ResponsePlan promotion (docs/RESPONSE_PLAN.md): when on, a
    /// conflict-verified companion-owned correction puts ONE authoritative acknowledgment
    /// constraint in the packet — she made the error, accept the correction plainly, no
    /// blame-sharing. Nothing else in the plan gains authority. Off by default; measured
    /// against both the genuine-correction and the agreement (Mad Hatter inversion)
    /// specimens before it ships on.
    /// </summary>
    public bool PromoteResponsePlan { get; set; }

    /// <summary>
    /// True shadow mode for the tuned renderer (docs/RENDERER_SHADOW.md): when enabled, each
    /// eligible turn's ResponsePlan is also rendered by the run-1c adapter through a local
    /// serve_tuned.py endpoint, both replies are scored by the deterministic renderer checks,
    /// and the pair is recorded as a shadow comparison. The shadow reply is never returned,
    /// stored as conversation, extracted from, or shown; disabling the flag restores the
    /// production path exactly because the production path never depended on it.
    /// </summary>
    public RendererShadowOptions RendererShadow { get; set; } = new();

    /// <summary>
    /// Source 3: deployment-imposed register restrictions (dimension -> closed-set value,
    /// e.g. "profanity" -> "avoid"). A SEPARATE authority from user preferences: it votes
    /// under hosting-config.* with a configuration-path evidence reference and can never
    /// masquerade as something the user asked for. Empty by default — nothing is
    /// restricted unless an operator explicitly configures it.
    /// </summary>
    public Dictionary<string, string> HostingRegisterRestrictions { get; set; } = [];

    /// <summary>Hard ceiling on tool executions in one turn (identical repeats stop earlier).</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;

    /// <summary>
    /// Layer the LLM intent classifier on top of the deterministic rules (requires a real model).
    /// The rules always run first and always win when they match; the model may only promote a
    /// plain chat message to a read-only intent for phrasings the rules don't know.
    /// </summary>
    public bool UseLlmIntentParser { get; set; }

    public int MaxAttentionItems { get; set; } = 5;
    public int AttentionTtlDays { get; set; } = 7;
    public int MaxAssociativeMemories { get; set; } = 2;
    public double AssociativeMinStrength { get; set; } = 0.65;
    public int MaxProceduresInContext { get; set; } = 2;
    public int MaxSharedPerspectivesInContext { get; set; } = 3;
}

/// <summary>Weights applied to each retrieval signal before summation.</summary>
public sealed class RetrievalWeights
{
    public double SemanticSimilarity { get; set; } = 1.0;
    public double KeywordOverlap { get; set; } = 0.6;
    public double Recency { get; set; } = 0.3;
    public double Importance { get; set; } = 0.3;
    public double Confidence { get; set; } = 0.2;
    public double ProjectAssociation { get; set; } = 0.5;
    public double OpenLoopBoost { get; set; } = 0.4;
}

/// <summary>
/// Configuration for renderer shadow mode (docs/RENDERER_SHADOW.md). All values are recorded
/// into every shadow row so the collected data names exactly which model produced it.
/// </summary>
public sealed class RendererShadowOptions
{
    /// <summary>Off by default; turning this off IS the rollback (no other state involved).</summary>
    public bool Enabled { get; set; }

    /// <summary>The serve_tuned.py endpoint hosting the adapter (Ollama-compatible chat API).</summary>
    public string Endpoint { get; set; } = "http://localhost:11435";

    /// <summary>sha256 of the adapter's adapter_model.safetensors, recorded per row.</summary>
    public string AdapterSha256 { get; set; } = "";

    /// <summary>Human-readable model identity ("run-1c on Qwen2.5-3B-Instruct aa8e7253"), recorded per row.</summary>
    public string ModelVersion { get; set; } = "";

    /// <summary>Per-call ceiling; a slow shadow is abandoned, never waited on by anything user-facing.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// The user-scoped canary (docs/RENDERER_SHADOW.md §8): when this names a user id, that
    /// user's eligible non-tool turns DISPLAY the tuned renderer's reply, with the production
    /// generator as immediate fallback on unavailability, timeout, empty output, or a critical
    /// fidelity failure. Empty (the default) means shadow-only for everyone. Clearing this
    /// setting is the complete rollback to the production renderer; no other user's routing
    /// is ever affected.
    /// </summary>
    public string CanaryUserId { get; set; } = "";

    /// <summary>
    /// Ollama num_gpu override for the renderer model (null = omit, Ollama decides). Zero
    /// pins the renderer to CPU — on a card too small to hold the chat model and the
    /// renderer together, that trades ~15 s of CPU prompt-eval for not evicting the chat
    /// model every turn, which costs far more. Ignored by serve_tuned/serve_cpu.
    /// </summary>
    public int? NumGpu { get; set; }

    /// <summary>
    /// Deployment secret (base64) for the keyed correlation tag on protected V3 shadow
    /// rows (spec rev-2.1). Absent = protected rows carry no content-derived identifier
    /// at all. Rotate by changing the key AND incrementing CorrelationKeyVersion.
    /// </summary>
    public string? CorrelationKeyBase64 { get; set; }

    /// <summary>Version stamped into correlation tags; increment on key rotation.</summary>
    public int CorrelationKeyVersion { get; set; } = 1;

    /// <summary>
    /// Ceiling for the in-turn canary render, separate from the shadow queue's generous one:
    /// past this, the fallback reply (already generated) is shown instead. The canary waits
    /// for the renderer; the user should barely wait for the canary.
    /// </summary>
    public int CanaryTimeoutSeconds { get; set; } = 25;
}
