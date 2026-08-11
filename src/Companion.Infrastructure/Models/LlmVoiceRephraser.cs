using Companion.Core.Abstractions;
using Companion.Core.Services;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// LLM-backed <see cref="IVoiceRephraser"/>: asks the conversational model to restyle a
/// deterministic draft in the companion's active persona. Guard-railed hard — the rewrite must
/// keep meaning, stay about the same length, and add nothing; anything suspicious (empty,
/// ballooned, failed) falls back to the draft, which is always a complete message by design.
/// </summary>
public sealed class LlmVoiceRephraser : IVoiceRephraser
{
    private readonly IChatModel _chat;
    private readonly IPersonalityService _personality;
    private readonly IProfileStore _profiles;
    private readonly ILogger<LlmVoiceRephraser> _logger;

    public LlmVoiceRephraser(
        IChatModel chat, IPersonalityService personality, IProfileStore profiles,
        ILogger<LlmVoiceRephraser> logger)
    {
        _chat = chat;
        _personality = personality;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<string> RephraseAsync(
        string userId, string draft, string situation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft))
            return draft;

        try
        {
            var profile = await _profiles.GetOrCreateAsync(userId, ct);
            var persona = _personality.Compose(profile);
            var system = persona + "\n\n" + Prompts.Format("rephrase.system", ("situation", situation));

            var rewritten = (await _chat.CompleteAsync(system, draft, ct: ct)).Text.Trim().Trim('"');

            // The rewrite may restyle, never rewrite reality: reject empties and balloons.
            if (rewritten.Length == 0 || rewritten.Length > draft.Length * 3 + 120)
            {
                _logger.LogDebug("Rephrase rejected (length {Length} vs draft {Draft}); using the draft.",
                    rewritten.Length, draft.Length);
                return draft;
            }

            return rewritten;
        }
        catch (Exception ex)
        {
            // A rephrase is cosmetic by contract — any failure quietly falls back to the draft.
            _logger.LogDebug(ex, "Rephrase failed; using the draft.");
            return draft;
        }
    }
}
