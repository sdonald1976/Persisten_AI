using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Maps a plain-language utterance to an <see cref="Intent"/> so the user never needs slash
/// commands. Returns <see cref="Intent.Chat"/> for anything that's just conversation. The
/// rule-based implementation ships by default (deterministic, offline); the LLM classifier can
/// be enabled on top of it for phrasings the rules don't know.
/// </summary>
public interface IIntentParser
{
    Task<Intent> ParseAsync(string input, CancellationToken ct = default);
}
