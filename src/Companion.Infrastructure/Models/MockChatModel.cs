using System.Text;
using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Deterministic, offline chat model. It doesn't generate novel language; instead it echoes
/// the continuity it was given — quoting the top remembered items — so the demo and tests can
/// verify that the right memory reached the prompt. Swap for a real model in prod.
/// </summary>
public sealed class MockChatModel : IChatModel
{
    public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
    {
        var bullets = systemPrompt
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var sb = new StringBuilder();
        if (bullets.Count == 0)
        {
            sb.Append("I don't have any prior context on that yet, but I'll remember it going forward. ");
            sb.Append($"You said: \"{userMessage}\"");
        }
        else
        {
            sb.Append("Continuing from what I remember");
            sb.Append(bullets.Count > 1 ? $" ({bullets.Count} relevant things): " : ": ");
            sb.Append(bullets[0]);
            sb.Append('.');
            if (systemPrompt.Contains("outdated", StringComparison.OrdinalIgnoreCase))
                sb.Append(" (Note: some of what I recall may be out of date, so tell me if it's changed.)");
            sb.Append($" Regarding \"{userMessage}\" — happy to pick that back up.");
        }

        return Task.FromResult(sb.ToString());
    }
}
