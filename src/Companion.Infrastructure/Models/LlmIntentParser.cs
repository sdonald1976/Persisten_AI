using System.Text.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// The LLM classifier the rule-based parser's docs always promised — layered ON TOP of the
/// rules, never instead of them. The deterministic rules run first: when they recognize an
/// intent, that answer stands (precise, instant, tested). Only a message the rules read as plain
/// chat is offered to the model, which may promote it to a known intent ("so what's rattling
/// around in that head of yours?" → ShareThoughts). Anything doubtful — parse failure, unknown
/// intent name, empty output — stays Chat, which is always safe: the worst failure mode is a
/// normal conversation turn.
/// </summary>
public sealed class LlmIntentParser : IIntentParser
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Intents the model may promote a chat message to. Deliberately excludes the
    /// destructive/stateful ones (Forget, Dispute, Privacy, identity/personality changes) — those
    /// only ever fire on the precise rule patterns the user can rely on.</summary>
    private static readonly HashSet<IntentKind> Promotable = new()
    {
        IntentKind.Recall,
        IntentKind.ListProjects,
        IntentKind.ListOpenLoops,
        IntentKind.ShareThoughts,
        IntentKind.Greeting,
    };

    private readonly IChatModel _chat;
    private readonly RuleBasedIntentParser _rules;
    private readonly ILogger<LlmIntentParser> _logger;

    public LlmIntentParser(IChatModel chat, RuleBasedIntentParser rules, ILogger<LlmIntentParser> logger)
    {
        _chat = chat;
        _rules = rules;
        _logger = logger;
    }

    public async Task<Intent> ParseAsync(string input, CancellationToken ct = default)
    {
        // Rules first — a deterministic hit is precise and final.
        var ruled = _rules.Parse(input);
        if (ruled.Kind != IntentKind.Chat)
            return ruled;

        // Very short or very long messages aren't worth a classification call.
        var text = (input ?? "").Trim();
        if (text.Length is < 8 or > 400)
            return ruled;

        try
        {
            var raw = (await _chat.CompleteAsync(Prompts.Get("intent.system"), text, format: ResponseFormat.Json, ct: ct)).Text;
            var dto = JsonSerializer.Deserialize<IntentDto>(StripFence(raw), Json);
            if (dto?.Intent is null)
                return ruled;

            if (!Enum.TryParse<IntentKind>(dto.Intent, ignoreCase: true, out var kind) || !Promotable.Contains(kind))
                return ruled;

            var argument = string.IsNullOrWhiteSpace(dto.Argument) ? null
                : dto.Argument.Trim() is { Length: <= 300 } a ? a : dto.Argument.Trim()[..300];
            _logger.LogDebug("LLM promoted a chat message to {Intent}.", kind);
            return new Intent { Kind = kind, Argument = argument };
        }
        catch (Exception ex)
        {
            // Classification is best-effort by contract: any failure means "it was just chat".
            _logger.LogDebug(ex, "Intent classification failed; treating as chat.");
            return ruled;
        }
    }

    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var nl = t.IndexOf('\n');
        if (nl < 0)
            return t;
        t = t[(nl + 1)..];
        var end = t.LastIndexOf("```", StringComparison.Ordinal);
        return end >= 0 ? t[..end] : t;
    }

    private sealed record IntentDto(string? Intent, string? Argument);
}
