using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Asks the configured extraction model to do its actual job once, at startup, on a sentence whose
/// answer is not in doubt.
///
/// <see cref="ModelPreflight"/> checks that each configured model <em>exists</em>. That is a
/// different question from whether it <em>works</em>, and the gap between them cost this project
/// every memory it should have formed: the extraction role was pointed at a model small enough to
/// answer "{}" to everything, which is present, responsive, fast, and completely useless. Presence
/// passed. Every unit test passed. The turn log read normally. She simply never remembered
/// anything, and appeared to only because the recent transcript is in her prompt.
///
/// One real call at startup turns that from a fault discovered weeks later into a line on boot.
/// It costs a small inference and runs in the background, which is a cheap price for the only
/// check that would have caught the worst bug this project has had.
/// </summary>
public sealed class ExtractionSelfTest
{
    /// <summary>
    /// Two unambiguous, durable facts in one plain sentence — a pet's name and its breed. If a
    /// model cannot find a memory here it cannot do this job at all, so a failure needs no
    /// interpretation.
    /// </summary>
    private const string Probe = "My dog is called Precious and she is a nine year old border collie.";

    private const string Reply = "She sounds lovely. Border collies are famously clever.";

    private readonly ILogger<ExtractionSelfTest> _logger;

    public ExtractionSelfTest(ILogger<ExtractionSelfTest> logger) => _logger = logger;

    /// <summary>
    /// True when the extractor proposed at least one candidate. Never throws: this is diagnostics,
    /// and must never be the reason the companion fails to start.
    /// </summary>
    public async Task<bool> RunAsync(IMemoryExtractor extractor, string userId, CancellationToken ct = default)
    {
        try
        {
            var exchange = new[]
            {
                Turn(userId, MessageRole.User, Probe),
                Turn(userId, MessageRole.Assistant, Reply),
            };

            var candidates = await extractor.ExtractAsync(userId, exchange, resolution: null, ct);
            if (candidates.Count > 0)
            {
                _logger.LogInformation(
                    "Extraction self-test passed: {Count} candidate(s) from the probe sentence.", candidates.Count);
                return true;
            }

            _logger.LogError(
                "Extraction self-test FAILED: the extraction model found nothing in \"{Probe}\". " +
                "Durable memory is not working — she will seem to remember things within a conversation, " +
                "because the transcript is in her prompt, and know nothing in the next one. The usual " +
                "cause is a model too small for the extraction prompt; check the Models:Extraction role.",
                Probe);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extraction self-test could not run; durable memory is unverified.");
            return false;
        }
    }

    private static Message Turn(string userId, MessageRole role, string content) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = Guid.Empty,
        UserId = userId,
        Role = role,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow,
    };
}
