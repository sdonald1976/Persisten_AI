using System.Text.RegularExpressions;
using Companion.Core.Abstractions;

namespace Companion.Core.Services;

/// <summary>
/// Cheap privacy baseline. It catches credentials and explicit off-record phrasing without making
/// a model call; an LLM classifier can wrap this for softer cases.
/// </summary>
public sealed partial class RuleBasedPrivacyClassifier : IPrivacyClassifier
{
    [GeneratedRegex(
        @"\b(off the record|don't remember this|do not remember this|keep this private|private conversation|between us)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivatePhrases();

    public Task<bool> ShouldSkipDerivedMemoryAsync(string userMessage, CancellationToken ct = default)
    {
        var skip = SecretDetector.LooksLikeSecret(userMessage) || PrivatePhrases().IsMatch(userMessage);
        return Task.FromResult(skip);
    }
}
