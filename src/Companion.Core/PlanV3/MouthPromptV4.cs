using System.Text;
using Companion.Core.Domain;

namespace Companion.PlanV3;

/// <summary>The two messages the mouth receives. Nothing else reaches it.</summary>
public readonly record struct MouthPrompt(string System, string User);

/// <summary>
/// THE plan/4 inference-time format, defined exactly once.
///
/// docs/RUN2_CURRICULUM_R5.md §4 declares it:
///
///     input  = system prompt, exactly as ContextPacket.Render() produces it
///            + CompactV4 serialization of the plan
///            + transcript window, oldest first
///     target = the utterance
///
/// ...but until now nothing assembled it. The components were production code and the glue was
/// prose, which is the one arrangement where a training corpus can be built against a format the
/// shipping renderer will never produce. Every row would be subtly wrong and nothing would say so.
///
/// So this class is the single definition, and it lives in production rather than in the data
/// factory on purpose: when Run-2's mouth is wired into the turn path it calls THIS, and the
/// corpus it was trained on was rendered by THIS. "The training row's input equals the shipping
/// renderer's input" is then true by construction rather than by review.
///
/// It is a pure function and nothing in the turn path calls it yet. Adding it changes no routing:
/// production remains Stheno on the plan/2 packet path, and native plan/4 still reaches no model.
/// </summary>
public static class MouthPromptV4
{
    /// <summary>Bumped only when the bytes change. Recorded on every row and every freeze.</summary>
    public const string FormatVersion = "mouth-prompt/4.0";

    /// <summary>
    /// The system message: the companion's own rendered context packet, unchanged. Not a
    /// bespoke "you are a renderer" preamble — the mouth occupies the seat the chat model
    /// occupies in TurnExecution, and that seat receives exactly this.
    /// </summary>
    public static string SystemMessage(ContextPacket packet) => packet.Render();

    /// <summary>
    /// The user message: the plan first, then the conversation window oldest-first, then the
    /// turn being answered. The plan leads because it conditions how the window is read.
    /// </summary>
    public static string UserMessage(
        global::Companion.PlanV3.PlanV3 plan,
        IReadOnlyList<(string Role, string Text)> transcript,
        string userMessage,
        string userName = "Scott",
        string companionName = "Ava")
    {
        // CompactV4 refuses a plan that is invalid or render-ineligible, which is the point: a
        // row can never be built from a plan the production serializer would not emit.
        var compact = PlanV4Codec.CompactV4(plan);

        var sb = new StringBuilder();
        sb.Append("RESPONSE PLAN:\n");
        sb.Append(compact.ReplaceLineEndings("\n"));
        if (!compact.EndsWith('\n'))
            sb.Append('\n');
        sb.Append("RECENT CONVERSATION:\n");
        foreach (var (role, text) in transcript)
            sb.Append('[').Append(Speaker(role, userName, companionName)).Append("] ").Append(text).Append('\n');
        sb.Append('[').Append(userName).Append("] ").Append(userMessage).Append('\n');
        sb.Append('\n').Append(companionName).Append("'s reply:");
        return sb.ToString();
    }

    /// <summary>Both messages together, for callers that want the whole input at once.</summary>
    public static MouthPrompt Build(
        ContextPacket packet,
        global::Companion.PlanV3.PlanV3 plan,
        IReadOnlyList<(string Role, string Text)> transcript,
        string userMessage,
        string userName = "Scott",
        string companionName = "Ava")
        => new(SystemMessage(packet), UserMessage(plan, transcript, userMessage, userName, companionName));

    private static string Speaker(string role, string userName, string companionName)
        => role.Equals("user", StringComparison.OrdinalIgnoreCase) ? userName : companionName;
}
