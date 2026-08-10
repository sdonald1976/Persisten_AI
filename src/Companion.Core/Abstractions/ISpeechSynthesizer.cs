namespace Companion.Core.Abstractions;

/// <summary>
/// Text-to-speech role: turns the companion's reply text into spoken audio, so a voice face can
/// play it back. The mirror image of <see cref="ITranscriber"/> — together they close the voice
/// loop (mic → STT → turn → TTS → speaker). Requires a separate audio-capable server (Piper via a
/// wrapper, Speaches, LocalAI, …); Ollama and LM Studio don't synthesize speech.
/// </summary>
public interface ISpeechSynthesizer
{
    /// <summary>
    /// Synthesizes <paramref name="text"/> to an audio clip. <paramref name="voice"/> overrides the
    /// configured default when given (null = use the default).
    /// </summary>
    Task<SpeechAudio> SynthesizeAsync(string text, string? voice = null, CancellationToken ct = default);
}

/// <summary>A synthesized audio clip: the raw bytes and the MIME type to serve/play them with.</summary>
public readonly record struct SpeechAudio(byte[] Audio, string ContentType);
