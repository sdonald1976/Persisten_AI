using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// Deterministic guard that flags text which obviously contains a credential, so it never gets
/// baked into durable memory. Conservative by design: it targets well-known secret shapes (API
/// keys, tokens, private-key blocks, explicit "password: …") to avoid nuking ordinary prose.
/// Not a DLP system — a first, cheap line of defense before persistence.
/// </summary>
public static class SecretDetector
{
    private static readonly Regex[] Patterns =
    {
        new(@"\bsk-[A-Za-z0-9]{16,}\b", RegexOptions.Compiled),                 // OpenAI-style keys
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled),          // GitHub tokens
        new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled),        // Slack tokens
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),                    // AWS access key id
        new(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled),              // Google API key
        new(@"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled), // JWT
        new(@"\b(?:password|passwd|api[_-]?key|secret|access[_-]?token|bearer)\b\s*[:=]\s*\S{6,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public static bool LooksLikeSecret(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (var rx in Patterns)
            if (rx.IsMatch(text))
                return true;
        return false;
    }
}
