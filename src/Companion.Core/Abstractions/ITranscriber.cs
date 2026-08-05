namespace Companion.Core.Abstractions;

/// <summary>
/// Speech-to-text role: transcribes an audio file to text (e.g. Whisper). The text is then fed
/// through a normal turn so spoken input is remembered like typed input. Requires a separate
/// audio-capable server — Ollama and LM Studio don't do audio.
/// </summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(Stream audio, string fileName, CancellationToken ct = default);
}
