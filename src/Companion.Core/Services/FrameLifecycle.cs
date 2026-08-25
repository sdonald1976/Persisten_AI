using Companion.Core.Domain;
using Companion.PlanV3;

namespace Companion.Core.Services;

/// <summary>
/// The smallest typed frame lifecycle: given the current session and what the turn asked
/// for, decide the transition. Pure, deterministic, no I/O — the store applies the result.
///
/// Three rules keep `InCharacterDetector` in its place:
///
///  1. **Entering requires an explicit request.** Detected in-character markup alone never
///     enters a frame. It may route a turn to production, and it may prompt an offer; it
///     does not create frame truth.
///  2. **Exiting is generous.** An explicit exit always exits, and ambiguity resolves TOWARD
///     exit — continuing a scene someone has left is the worse failure by a distance.
///  3. **Every transition records its evidence**, so the two failures a detector cannot
///     distinguish are separable afterwards.
/// </summary>
public static class FrameLifecycle
{
    /// <summary>What the turn asked for, as cognition read it — never as a regex guessed it.</summary>
    public enum Request
    {
        /// <summary>Nothing frame-related was asked. The default for ordinary turns.</summary>
        None,

        /// <summary>An explicit request to begin roleplay, or explicit acceptance of an offer.</summary>
        ExplicitEnter,

        /// <summary>An explicit change of character, viewpoint, narration or scene.</summary>
        ExplicitSwitch,

        /// <summary>An explicit exit or stop.</summary>
        ExplicitExit,

        /// <summary>In-character markup was detected and nothing explicit was said. A HINT.</summary>
        DetectedInCharacter,

        /// <summary>Something that might be an exit and might be dialogue. Rule 2 applies.</summary>
        AmbiguousExit,
    }

    public sealed record Decision(
        FrameTransition? Transition,
        string Cause,
        bool StartsSession = false,
        bool EndsSession = false)
    {
        /// <summary>No frame this turn: an ordinary real turn, serializing no FRAME section.</summary>
        public static Decision NoFrame(string cause) => new(null, cause);
    }

    /// <param name="hasActiveSession">Whether an Active FrameSession exists for this conversation.</param>
    public static Decision Decide(Request request, bool hasActiveSession)
        => (request, hasActiveSession) switch
        {
            // Rule 1: a hint never creates frame truth.
            (Request.DetectedInCharacter, false) =>
                Decision.NoFrame("detected-in-character: hint only, no explicit request"),

            (Request.ExplicitEnter, false) =>
                new(FrameTransition.enter, "explicit-enter", StartsSession: true),

            // Already in a frame: re-entering is just continuing it.
            (Request.ExplicitEnter, true) => new(FrameTransition.@continue, "already-in-frame"),

            (Request.ExplicitSwitch, true) => new(FrameTransition.switchScene, "explicit-switch"),
            (Request.ExplicitSwitch, false) =>
                Decision.NoFrame("explicit-switch with no active session: nothing to switch"),

            // Rule 2: exits are generous, and ambiguity resolves toward exit.
            (Request.ExplicitExit, true) =>
                new(FrameTransition.exit, "explicit-exit", EndsSession: true),
            (Request.AmbiguousExit, true) =>
                new(FrameTransition.exit, "ambiguous-exit-resolved-toward-exit", EndsSession: true),

            // An exit with nothing to exit is already the desired state.
            (Request.ExplicitExit, false) => Decision.NoFrame("explicit-exit with no active session"),
            (Request.AmbiguousExit, false) => Decision.NoFrame("ambiguous-exit with no active session"),

            (Request.DetectedInCharacter, true) => new(FrameTransition.@continue, "in-frame"),
            (Request.None, true) => new(FrameTransition.@continue, "in-frame"),
            _ => Decision.NoFrame("no frame"),
        };

    /// <summary>
    /// Whether this turn's content is fictional, for the downstream separation. True only
    /// while a frame is actually live — and NOT on the exit turn, because exiting restores
    /// real rules on the turn that carries it rather than the one after.
    /// </summary>
    public static bool IsFictionTurn(Decision decision)
        => decision.Transition is FrameTransition.enter
            or FrameTransition.@continue
            or FrameTransition.switchScene;
}
