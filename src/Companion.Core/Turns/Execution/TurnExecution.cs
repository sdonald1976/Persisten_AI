using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Companion.Core.Turns.Execution;

/// <summary>
/// Everything execution needs, as one typed request rather than fifteen positional
/// parameters. Assembled by the caller from stages that have already run.
/// </summary>
public sealed record TurnExecutionRequest
{
    public required Guid TraceId { get; init; }
    public required string UserId { get; init; }
    public required Guid ConversationId { get; init; }

    /// <summary>The message being answered. Its id is the turn's evidence identity.</summary>
    public required Guid SourceMessageId { get; init; }

    public required string PromptText { get; init; }
    public required ContextPacket Packet { get; init; }
    public required IReadOnlyList<Message> Recent { get; init; }
    public required ResponsePlan Plan { get; init; }
    public required ToolLoop.Outcome ToolOutcome { get; init; }

    /// <summary>Roleplay routing: an in-character turn stays on production.</summary>
    public required bool InCharacter { get; init; }

    /// <summary>Privacy: a sensitive turn is rendered but never recorded.</summary>
    public required bool Sensitive { get; init; }

    public string? CompanionName { get; init; }

    /// <summary>Native plan/4 material, carried for the canary observation only. Never sent.</summary>
    public PlanV3.PlanV3? NativeV3 { get; init; }
    public string? NativeBuildError { get; init; }
    public IReadOnlyList<string> NativeLintRejections { get; init; } = [];
    public PlanV3.AssemblyReport? NativeAssembly { get; init; }
    public int? NativeCompactV4Chars { get; init; }
    public string? NativeFrameTransition { get; init; }

    public IProgress<string>? TokenSink { get; init; }
}

/// <summary>
/// A gate refusal, returned so the CALLER can record it. The gate can change the displayed
/// reply, so the decision is execution's; writing the comparison row is not.
/// </summary>
public sealed record GateRefusal
{
    public required string? Reason { get; init; }
    public required bool Enforced { get; init; }
}

/// <summary>What execution produced, and how it chose.</summary>
public sealed record TurnExecutionResult
{
    /// <summary>The production model's reply, after the echo filter, before any canary swap.</summary>
    public required string ProductionCandidate { get; init; }

    /// <summary>The renderer's reply when a canary turn produced a usable one.</summary>
    public string? RendererCandidate { get; init; }

    /// <summary>What the user actually gets. Selected exactly once.</summary>
    public required string Displayed { get; init; }

    /// <summary>"production" or "run-1c".</summary>
    public required string SelectedRenderer { get; init; }

    /// <summary>Why production was used on a canary turn. Null when nothing fell back.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>Whether this turn was eligible for the canary at all — a later shadow fact.</summary>
    public required bool CanaryTurn { get; init; }

    /// <summary>The exact bytes sent to the model, rendered once.</summary>
    public required string RenderedPrompt { get; init; }

    /// <summary>Generation metadata: finish reason, rounds, tokens, model.</summary>
    public required ChatCompletion Generation { get; init; }

    public GateRefusal? Refusal { get; init; }

    public required IReadOnlyList<DecisionRecord> Decisions { get; init; }
}

/// <summary>
/// The fifth stage of a turn: run the tools, call the model, and decide what is actually
/// displayed.
///
/// It begins with prepared context, a plan and a packet, and ends the moment the final reply
/// is selected. It persists nothing. Every post-turn effect — the message itself, extraction,
/// reflection, mood, relationship, attention, procedures — belongs to the caller and happens
/// after this returns.
///
/// The one boundary worth stating precisely: a runtime check that can CHANGE what is
/// displayed is execution's, and a check that only records belongs to observability. So the
/// canary's critical-failure guard and the reply gate live here, because both can replace the
/// reply; the plan-fidelity checks and the shadow observation do not, because neither can.
/// The gate's comparison ROW is likewise not written here — the decision is execution's, the
/// record is the caller's.
///
/// Production stays authoritative. The canary is off operationally, an in-character or
/// tool-using turn is never eligible, and native plan/4 reaches no model: it is carried into
/// the observation for recording and nowhere else.
/// </summary>
public sealed class TurnExecution(
    ToolLoop toolLoop,
    IReplyGenerator replyGenerator,
    IRendererShadow rendererShadow,
    ILogger<TurnExecution> logger,
    IReplyGate? gate = null,
    IOptions<SafetyOptions>? safety = null,
    IOptions<CompanionOptions>? options = null)
{
    private readonly IReplyGate _gate = gate ?? new AlwaysOpenGate();
    private readonly SafetyOptions _safety = safety?.Value ?? new SafetyOptions();
    private readonly SthenoFreeOptions _sthenoFree = options?.Value.SthenoFree ?? new();
    private readonly string _mouthModelVersion =
        options?.Value.RendererShadow.Mouth.ModelVersion is { Length: > 0 } v ? v : "mouth";

    /// <summary>
    /// The bounded tool loop, driven by the executive planner. It gets a COMPACT planning
    /// context rather than the full packet.
    /// </summary>
    public async Task<(ToolLoop.Outcome Outcome, DecisionRecord Decision)> RunToolsAsync(
        string userId,
        IReadOnlyList<Message> recent,
        IReadOnlyList<RetrievalResult> selectedMemories,
        string? resolvedProjectName,
        string promptText,
        Guid traceId,
        CancellationToken ct = default)
    {
        var planningContext = BuildPlanningContext(recent, selectedMemories, resolvedProjectName);
        var outcome = await toolLoop.RunAsync(userId, planningContext, promptText, traceId, ct);

        return (outcome, new DecisionRecord
        {
            Stage = "tools",
            Decider = outcome.PlanningRounds > 0 ? "model" : "rule",
            Verdict = outcome.Calls.Count == 0
                ? "none" : string.Join(",", outcome.Calls.Select(c => c.Tool)),
        });
    }

    /// <summary>
    /// Renders the prompt, generates the reply, runs the canary if this turn is eligible, and
    /// applies the reply gate. Returns the selected reply; stores nothing.
    /// </summary>
    public async Task<TurnExecutionResult> ExecuteAsync(
        TurnExecutionRequest request, CancellationToken ct = default)
    {
        var decisions = new List<DecisionRecord>();

        // The Stheno-free route: this user's turn is anchored on the native plan/4 and the
        // mouth, and the conversational model is never called - not for generation, not as a
        // fallback. Decided before anything else so no code below it can reach the generator.
        if (_sthenoFree.AppliesTo(request.UserId))
            return await ExecuteSthenoFreeAsync(request, decisions, ct);

        // The user-scoped renderer canary: on this user's eligible non-tool turns the tuned
        // renderer's reply is DISPLAYED and production is the immediate fallback. Decided
        // before generation so streaming can be handled — production tokens are not forwarded
        // on a canary turn, because the displayed reply may differ.
        //
        // CAPABILITY ROUTING, not content blocking. Run-1c's corpus contains no roleplay, so
        // an in-character turn stays on production, the model proven to handle it. That
        // restricts no subject matter: production answers it in full.
        // Run-2 needs a native plan/4 and the packet; without either it has nothing to render and
        // the turn stays on production. Tool turns and in-character turns are excluded for the
        // same reason they are excluded from the run-1c canary: the corpus never covered them.
        var mouthEligible = request.ToolOutcome.Calls.Count == 0
            && !request.InCharacter
            && request.NativeV3 is not null;

        var mouthCanaryTurn = rendererShadow.IsMouthCanaryFor(request.UserId) && mouthEligible;

        // Exactly one candidate can ever be displayed. When the mouth canary owns this turn the
        // run-1c canary stands down, so there is no arrangement in which two models both produce
        // a reply that could be shown - the user sees one reply and one only.
        var canaryTurn = !mouthCanaryTurn
            && rendererShadow.IsCanaryFor(request.UserId)
            && request.ToolOutcome.Calls.Count == 0
            && !request.InCharacter;

        // Rendered once, here, so what diagnostics show is the string that was sent rather
        // than a second rendering that might differ.
        var renderedPrompt = request.Packet.Render();

        // Streaming is suppressed for any canary turn: a token sink would show the user the
        // production reply as it is generated, and then a different reply would replace it.
        var generated = await replyGenerator.GenerateAsync(
            renderedPrompt, request.PromptText,
            canaryTurn || mouthCanaryTurn ? null : request.TokenSink,
            request.CompanionName, ct);

        // Her own transcript is in the prompt, and she sometimes continues it instead of
        // replying. This is the only place that can catch it, because it needs the
        // conversation to compare against, which the generator does not have.
        var production = EchoedTurnFilter.Strip(generated.Text, request.Recent);
        if (!ReferenceEquals(production, generated.Text) && production.Length != generated.Text.Length)
        {
            logger.LogWarning(
                "Reply for {UserId} began by repeating an earlier turn verbatim ({Removed} chars removed).",
                request.UserId, generated.Text.Length - production.Length);
        }

        var response = production;
        string? rendererCandidate = null;
        var selectedRenderer = "production";
        string? fallbackReason = null;

        if (mouthCanaryTurn)
        {
            var mouthResult = await rendererShadow.RenderMouthForDisplayAsync(
                MouthObservation(request, response), record: !request.Sensitive, ct);

            selectedRenderer = mouthResult is { CriticalFailure: false } ? "run-2" : "production";
            if (selectedRenderer == "run-2")
            {
                rendererCandidate = mouthResult!.Reply;
                response = rendererCandidate;
            }
            else
            {
                fallbackReason = mouthResult is null
                    ? "mouth unavailable or timed out"
                    : $"critical fidelity failure: {string.Join("; ", mouthResult.Violations)}";
            }

            decisions.Add(new DecisionRecord
            {
                Stage = "mouth.canary", Decider = "config",
                Verdict = selectedRenderer == "run-2" ? "displayed-run2" : "fallback-production",
                Reason = mouthResult is null ? "mouth unavailable or timed out"
                    : mouthResult.CriticalFailure
                        ? $"critical fidelity failure: {string.Join("; ", mouthResult.Violations)}"
                        : $"latency {mouthResult.LatencyMs}ms",
            });
        }
        else if (mouthEligible && rendererShadow.IsMouthObserving)
        {
            // Shadow: run-2 renders beside the reply the user is already getting, and the pair is
            // recorded. Nothing here can change the displayed reply, and nothing waits on it.
            if (!request.Sensitive)
                rendererShadow.ObserveMouth(MouthObservation(request, response));
        }

        if (canaryTurn)
        {
            var canaryResult = await rendererShadow.RenderForDisplayAsync(
                new RendererShadowObservation
                {
                    TraceId = request.TraceId,
                    UserId = request.UserId,
                    SourceMessageId = request.SourceMessageId,
                    ConversationId = request.ConversationId,
                    Plan = request.Plan,
                    Transcript = request.Recent
                        .TakeLast(4)
                        .Select(m => (m.Role == MessageRole.User ? "user" : "assistant", m.Content))
                        .ToList(),
                    UserMessage = request.PromptText,
                    ProductionResponse = response,
                    NativeV3 = request.NativeV3,
                    NativeBuildError = request.NativeBuildError,
                    NativeLintRejections = request.NativeLintRejections,
                    NativeAssembly = request.NativeAssembly,
                    NativeCompactV4Chars = request.NativeCompactV4Chars,
                    NativeFrameTransition = request.NativeFrameTransition,
                }, record: !request.Sensitive, ct);

            selectedRenderer = canaryResult is { CriticalFailure: false } ? "run-1c" : "production";
            if (selectedRenderer == "run-1c")
            {
                rendererCandidate = canaryResult!.Reply;
                response = rendererCandidate;
            }
            else
            {
                fallbackReason = canaryResult is null
                    ? "renderer unavailable or timed out"
                    : $"critical fidelity failure: {string.Join("; ", canaryResult.Violations)}";
            }

            decisions.Add(new DecisionRecord
            {
                Stage = "renderer.canary", Decider = "config",
                Verdict = selectedRenderer == "run-1c" ? "displayed-run1c" : "fallback-production",
                Reason = canaryResult is null ? "renderer unavailable or timed out"
                    : canaryResult.CriticalFailure
                        ? $"critical fidelity failure: {string.Join("; ", canaryResult.Violations)}"
                        : $"latency {canaryResult.LatencyMs}ms",
            });

            // The sink was withheld from the generator; deliver the chosen reply once, whole.
            request.TokenSink?.Report(response);
        }

        // The reply gate, on what she is actually about to say. It judges meaning rather than
        // form, and runs before storage so a refused reply is never what the next turn reads
        // back as context.
        //
        // In shadow mode the verdict is recorded and the reply goes out unchanged. That is the
        // default even when the gate is on: a gate whose false-positive rate has never been
        // measured should not decide what she may say, and the only way to measure it is to
        // watch it be wrong without cost.
        var (gatedResponse, refusal) = await ApplyGateAsync(response, request, decisions, ct);
        response = gatedResponse;

        return new TurnExecutionResult
        {
            ProductionCandidate = production,
            RendererCandidate = rendererCandidate,
            Displayed = response,
            SelectedRenderer = selectedRenderer,
            FallbackReason = fallbackReason,
            CanaryTurn = canaryTurn,
            RenderedPrompt = renderedPrompt,
            Generation = generated,
            Refusal = refusal,
            Decisions = decisions,
        };
    }

    /// <summary>
    /// The compact planning context the executive tool planner reads — deliberately not the
    /// full packet, because the planner decides whether to call a tool rather than what to
    /// say. Moved byte-for-byte: this string reaches a model, so even its ellipsis characters
    /// are part of the contract.
    /// </summary>

    /// <summary>
    /// The reply gate, on what she is actually about to say - shared by the production path
    /// and the Stheno-free route, because the gate's authority is over MEANING and does not
    /// care which renderer produced the words. In shadow mode the verdict is recorded and the
    /// reply goes out unchanged.
    /// </summary>
    private async Task<(string Response, GateRefusal? Refusal)> ApplyGateAsync(
        string response, TurnExecutionRequest request, List<DecisionRecord> decisions,
        CancellationToken ct)
    {
        if (!_gate.IsEnabled)
            return (response, null);

        var verdict = await _gate.ReviewAsync(response, request.PromptText, ct);
        decisions.Add(new DecisionRecord
        {
            Stage = "reply.gate", Decider = "model",
            Verdict = verdict.Allow ? "allow"
                : _safety.Mode == GateMode.Enforce ? "block-enforced" : "block-shadow",
            Reason = verdict.Allow ? null : verdict.Reason,
        });
        if (verdict.Allow)
            return (response, null);

        var enforcing = _safety.Mode == GateMode.Enforce;
        logger.LogWarning(
            "Reply gate refused a reply for {UserId} ({Mode}): {Reason}",
            request.UserId, enforcing ? "enforced" : "shadow only", verdict.Reason);

        // Returned rather than recorded: the row is the caller's to write.
        var refusal = new GateRefusal { Reason = verdict.Reason, Enforced = enforcing };
        return (enforcing ? _safety.Replacement : response, refusal);
    }

    /// <summary>
    /// One construction, used by the shadow, the canary and the Stheno-free route. If these
    /// were built separately the thing measured in shadow would not be the thing displayed,
    /// and the shadow would stop being evidence about it.
    /// </summary>
    private static RendererShadowObservation MouthObservation(TurnExecutionRequest r, string produced)
        => new()
        {
            TraceId = r.TraceId,
            UserId = r.UserId,
            SourceMessageId = r.SourceMessageId,
            ConversationId = r.ConversationId,
            Plan = r.Plan,
            Packet = r.Packet,
            Transcript = r.Recent
                .TakeLast(4)
                .Select(m => (m.Role == MessageRole.User ? "user" : "assistant", m.Content))
                .ToList(),
            UserMessage = r.PromptText,
            ProductionResponse = produced,
            NativeV3 = r.NativeV3,
            NativeBuildError = r.NativeBuildError,
            NativeLintRejections = r.NativeLintRejections,
            NativeAssembly = r.NativeAssembly,
            NativeCompactV4Chars = r.NativeCompactV4Chars,
            NativeFrameTransition = r.NativeFrameTransition,
        };

    /// <summary>
    /// The Stheno-free turn: the native plan/4, rendered by the mouth, with a deterministic
    /// plan rendering as the only fallback. The conversational model is unreachable from this
    /// method - it takes no path to <see cref="IReplyGenerator"/>, which is the property the
    /// route exists for and the property its tests pin.
    ///
    /// Tool turns are allowed through (the plan carries the tool contributions the assembler
    /// granted); an in-character turn skips the mouth (its corpus never covered roleplay) and
    /// goes straight to the deterministic rendering; a turn with no native plan at all falls
    /// to the typed honest clarification. Nothing here can invoke any other renderer.
    /// </summary>
    private async Task<TurnExecutionResult> ExecuteSthenoFreeAsync(
        TurnExecutionRequest request, List<DecisionRecord> decisions, CancellationToken ct)
    {
        var fallback = request.NativeV3 is null
            ? global::Companion.PlanV3.DeterministicMouth.HonestFailure
            : global::Companion.PlanV3.DeterministicMouth.Render(request.NativeV3);

        RendererCanaryResult? mouth = null;
        var mouthAttempted = request.NativeV3 is not null && !request.InCharacter;
        if (mouthAttempted)
            mouth = await rendererShadow.RenderMouthForDisplayAsync(
                MouthObservation(request, fallback), record: !request.Sensitive, ct);

        string response;
        string selectedRenderer;
        string? fallbackReason = null;
        if (mouth is { CriticalFailure: false })
        {
            response = mouth.Reply;
            selectedRenderer = "run-2.1";
        }
        else
        {
            response = fallback;
            selectedRenderer = request.NativeV3 is null ? "honest-failure" : "plan-deterministic";
            fallbackReason = !mouthAttempted
                ? request.NativeV3 is null
                    ? "no native plan/4 was built: typed honest clarification"
                    : "in-character turn: deterministic plan rendering (mouth corpus has no roleplay)"
                : mouth is null
                    ? "mouth unavailable or timed out"
                    : $"critical fidelity failure: {string.Join("; ", mouth.Violations)}";
        }

        // The measured claim, not the architectural one: how many times the conversational
        // role was actually called on this flow so far. The route's contract is zero, and a
        // nonzero count here is a bug worth a loud log even though the reply already avoided
        // the result.
        var conversationCalls = ModelCallScope.Snapshot()
            .Count(c => string.Equals(c.Role, "conversation", StringComparison.Ordinal));
        if (conversationCalls > 0)
            logger.LogError(
                "Stheno-free turn for {UserId} observed {Calls} conversational-model call(s). "
                + "The displayed reply did not use them, but the route's zero-call contract is broken.",
                request.UserId, conversationCalls);

        decisions.Add(new DecisionRecord
        {
            Stage = "route.stheno-free", Decider = "config",
            Verdict = selectedRenderer switch
            {
                "run-2.1" => "displayed-run2.1",
                "honest-failure" => "honest-failure",
                _ => "fallback-deterministic",
            },
            Reason = (fallbackReason ?? $"latency {mouth?.LatencyMs}ms")
                     + $"; conversation-model calls: {conversationCalls}",
        });

        var (gated, refusal) = await ApplyGateAsync(response, request, decisions, ct);
        response = gated;

        // The sink was never given to any generator on this route; deliver once, whole.
        request.TokenSink?.Report(response);

        return new TurnExecutionResult
        {
            ProductionCandidate = fallback,
            RendererCandidate = mouth?.Reply,
            Displayed = response,
            SelectedRenderer = selectedRenderer,
            FallbackReason = fallbackReason,
            CanaryTurn = false,
            RenderedPrompt = "",
            Generation = ChatCompletion.FromText(response,
                selectedRenderer == "run-2.1" ? _mouthModelVersion : selectedRenderer),
            Refusal = refusal,
            Decisions = decisions,
        };
    }

    private static string BuildPlanningContext(
        IReadOnlyList<Message> recent, IReadOnlyList<RetrievalResult> selectedMemories, string? project)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Recent conversation (newest last):");
        foreach (var message in recent.OrderBy(m => m.Timestamp).TakeLast(6))
        {
            var content = message.Content.Length <= 200 ? message.Content : message.Content[..200] + "â€¦";
            sb.AppendLine($"- {(message.Role == MessageRole.User ? "user" : "assistant")}: {content}");
        }
        sb.AppendLine(project is null ? "Detected project: (none)" : $"Detected project: {project}");
        if (selectedMemories.Count == 0)
        {
            sb.AppendLine("Automatic retrieval found nothing relevant for this message.");
        }
        else
        {
            sb.AppendLine($"Automatic retrieval already found {selectedMemories.Count} memories " +
                "(judge whether these already cover the need):");
            foreach (var retrieved in selectedMemories.Take(3))
            {
                var summary = retrieved.Memory.Content;
                sb.AppendLine($"- {(summary.Length <= 150 ? summary : summary[..150] + "â€¦")}");
            }
        }
        return sb.ToString();
    }

    /// <summary>The gate when none is injected: on for nobody, and honest about it.</summary>
    private sealed class AlwaysOpenGate : IReplyGate
    {
        public bool IsEnabled => false;

        public Task<GateVerdict> ReviewAsync(string reply, string userMessage, CancellationToken ct = default)
            => Task.FromResult(GateVerdict.Allowed);
    }
}
