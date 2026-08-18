using System.Text.RegularExpressions;
using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Asks a small chat model whether a reply should be sent, and allows it whenever the answer is
/// anything other than an unambiguous refusal.
///
/// Every branch here that is not the happy path ends in <see cref="GateVerdict.Allowed"/>, and that
/// is the design rather than a set of defensive afterthoughts. A gate on the conversational path is
/// the one component where being wrong is worse than being absent: a miss lets through something
/// rare, while a false positive silences an ordinary conversation and is indistinguishable from a
/// crash. So a model that is missing, slow, overloaded, or simply chattier than it was asked to be
/// cannot stop a turn.
/// </summary>
public sealed class LlmReplyGate : IReplyGate
{
    private const int MaxChars = 4000;

    /// <summary>
    /// The word, not the prefix. The first version matched <c>BLOCK</c> with StartsWith, so a reply
    /// opening "blocked?" was refused — and "Blocking this would be wrong" would have gone the same
    /// way, the gate silencing a conversation because the model mused about gates.
    /// </summary>
    private static readonly Regex Block = new(@"\bBLOCK\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IChatModel _chat;
    private readonly SafetyOptions _options;
    private readonly ILogger<LlmReplyGate> _logger;

    public LlmReplyGate(IChatModel chat, SafetyOptions options, ILogger<LlmReplyGate> logger)
    {
        _chat = chat;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<GateVerdict> ReviewAsync(string reply, string userMessage, CancellationToken ct = default)
    {
        // Nothing to judge, and an inference that costs a turn's latency to conclude so. An empty
        // reply is a generation failure the turn handles elsewhere, not a safety question.
        if (!IsEnabled || string.IsNullOrWhiteSpace(reply))
            return GateVerdict.Allowed;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        string answer;
        try
        {
            var judgement = await _chat.CompleteAsync(
                Companion.Core.Services.Prompts.Get("safety.gate"),
                $"The person said:\n{Trim(userMessage)}\n\nThe companion is about to reply:\n{Trim(reply)}\n\nOK or BLOCK?",
                ct: timeout.Token);
            answer = judgement.Text ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The turn itself was cancelled. That is the caller's business, not a gate failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Reply gate timed out after {Seconds}s; allowing the reply.", _options.TimeoutSeconds);
            return GateVerdict.Allowed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reply gate failed; allowing the reply.");
            return GateVerdict.Allowed;
        }

        return Interpret(answer);
    }

    /// <summary>
    /// Reads the verdict off the first non-empty line only. A small model asked a yes/no question
    /// will sometimes explain itself at length afterwards, and a paragraph about what would be
    /// blockable is not a refusal of the reply in front of it.
    /// </summary>
    private GateVerdict Interpret(string answer)
    {
        var line = answer
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        var match = line is null ? null : Block.Match(line);
        if (match is not { Success: true })
            return GateVerdict.Allowed;

        var reason = line![(match.Index + match.Length)..].Trim(' ', ':', '-', '—', '.', '\t');
        if (reason.Length == 0)
            reason = "no reason given";

        // Enforcement is the mode's decision, and it is recorded on the verdict rather than left to
        // be inferred later from a config flag whose value at the time is not in the record.
        return new GateVerdict(false, reason, _options.Mode == GateMode.Enforce);
    }

    private static string Trim(string text)
        => text.Length > MaxChars ? text[..MaxChars] : text;
}

/// <summary>
/// The gate when there isn't one: reports itself off, is never called, and costs nothing.
///
/// Registered whenever the gate is disabled or no real chat model is configured, so the rest of the
/// companion depends on <see cref="IReplyGate"/> unconditionally and never on a nullable one.
/// </summary>
public sealed class OpenReplyGate : IReplyGate
{
    public bool IsEnabled => false;

    public Task<GateVerdict> ReviewAsync(string reply, string userMessage, CancellationToken ct = default)
        => Task.FromResult(GateVerdict.Allowed);
}
