using Companion.Core.Domain;

namespace Companion.Core.Abstractions;

/// <summary>
/// Maps a plain-language utterance to an <see cref="Intent"/> so the user never needs slash
/// commands. Returns <see cref="Intent.Chat"/> for anything that's just conversation. A
/// rule-based implementation ships by default; an LLM tool-calling parser can replace it later.
/// </summary>
public interface IIntentParser
{
    Intent Parse(string input);
}
