using Companion.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Advisory privacy classifier for durable derived memory. The deterministic classifier runs
/// first; the model catches softer sensitive cases and fails to "remember" on errors.
/// </summary>
public sealed class LlmPrivacyClassifier : IPrivacyClassifier
{
    private const int MaxMessageChars = 4000;

    private readonly IChatModel _chat;
    private readonly IPrivacyClassifier _fallback;
    private readonly ILogger<LlmPrivacyClassifier> _logger;

    public LlmPrivacyClassifier(IChatModel chat, IPrivacyClassifier fallback, ILogger<LlmPrivacyClassifier> logger)
    {
        _chat = chat;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<bool> ShouldSkipDerivedMemoryAsync(string userMessage, CancellationToken ct = default)
    {
        if (await _fallback.ShouldSkipDerivedMemoryAsync(userMessage, ct))
            return true;

        var message = userMessage.Length > MaxMessageChars ? userMessage[..MaxMessageChars] : userMessage;
        try
        {
            var verdict = await _chat.CompleteAsync(
                SystemPrompt,
                $"User message:\n{message}\n\nShould durable derived memory be SKIPPED or REMEMBERED?",
                jsonMode: false,
                ct: ct);

            var answer = verdict.Text.Trim();
            var skip = answer.Contains("SKIP", StringComparison.OrdinalIgnoreCase)
                && !answer.Contains("REMEMBER", StringComparison.OrdinalIgnoreCase);
            _logger.LogDebug("Privacy classifier verdict: {Verdict} -> {Decision}",
                answer.Length > 40 ? answer[..40] + "..." : answer, skip ? "SKIP" : "REMEMBER");
            return skip;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Privacy classifier failed; using deterministic privacy decision.");
            return false;
        }
    }

    private const string SystemPrompt =
        "You decide whether a user message should be excluded from durable derived memory. " +
        "Answer exactly SKIP or REMEMBER. SKIP secrets, credentials, explicit off-record/private " +
        "phrasing, highly sensitive third-party details, or content the assistant should not bring " +
        "up proactively later. REMEMBER ordinary preferences, projects, plans, and stable facts.";
}
