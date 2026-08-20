using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Companion.Core.Domain;

/// <summary>
/// The architectural invariant these types enforce: STRINGS REPRESENT LANGUAGE, TYPES
/// REPRESENT COGNITION. A move or an intent is a decision the system made, and a decision
/// modeled as a raw string can be misspelled, half-compared, and silently forked; an enum
/// cannot. The friendly kebab labels ("answers-open-question", "clarify") survive unchanged
/// at every JSON/diagnostic/capture boundary — they are derived mechanically from the enum
/// names, and <see cref="CognitionLabels.ToKebab{T}"/> is the one place that derivation
/// lives.
/// </summary>
[JsonConverter(typeof(KebabEnumConverter<ConversationMove>))]
public enum ConversationMove
{
    NewTopic,
    ContinuesThread,
    AnswersOpenQuestion,
    ResolvesReference,
    Correction,

    /// <summary>Correction-SHAPED words whose asserted value already matches the
    /// companion's preceding claim — emphatic agreement, not correction. The Mad Hatter
    /// inversion: "No, it was actually the Cheshire Cat" after she said Cheshire Cat.
    /// No error exists; contrition would be invented.</summary>
    ConfirmsClaim,
}

/// <summary>How a reference resolution was reached — see ReferenceResolution for what each
/// grade is allowed to do downstream.</summary>
[JsonConverter(typeof(KebabEnumConverter<ResolutionConfidence>))]
public enum ResolutionConfidence
{
    /// <summary>Parsed from something exact: an enumerated item, the user's own prior words.</summary>
    Exact,

    /// <summary>A pronoun with exactly one user-introduced candidate in the window.</summary>
    Unambiguous,

    /// <summary>Newest plausible entity — retrieval-grade only, never extraction-grade.</summary>
    Guess,
}

/// <summary>What Ava should DO this turn. Acts, never prose — personality stays downstream.</summary>
[JsonConverter(typeof(KebabEnumConverter<TurnIntent>))]
public enum TurnIntent
{
    /// <summary>No rule cleared the bar; continue naturally. The preferred answer over a
    /// confidently wrong one.</summary>
    Unknown,

    AnswerQuestion,
    Acknowledge,
    RespondToAnswer,
    Clarify,
    ContinueTopic,
    AcceptCorrection,
    FollowTopicChange,
    AdmitUnknown,
    RequestDirective,
}

public static class CognitionLabels
{
    /// <summary>PascalCase → kebab-case, cached per enum value: AnswersOpenQuestion →
    /// "answers-open-question". The single source of every friendly label.</summary>
    public static string ToKebab<T>(this T value) where T : struct, Enum
        => LabelCache<T>.Labels[value];

    private static class LabelCache<T> where T : struct, Enum
    {
        public static readonly IReadOnlyDictionary<T, string> Labels =
            Enum.GetValues<T>().ToDictionary(v => v, v => Kebab(v.ToString()));
    }

    private static string Kebab(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 4);
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && sb.Length > 0)
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>Serializes cognition enums as their kebab labels, so diagnostics JSON keeps
/// reading "answers-open-question" and every existing consumer (dashboard, soak harness,
/// capture miners) is untouched by the internal typing.</summary>
public sealed class KebabEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly IReadOnlyDictionary<string, T> Reverse =
        Enum.GetValues<T>().ToDictionary(v => v.ToKebab(), v => v, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() is { } label && Reverse.TryGetValue(label, out var value)
            ? value
            : throw new JsonException($"'{reader.GetString()}' is not a {typeof(T).Name} label");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToKebab());
}
