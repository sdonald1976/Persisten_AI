using System.Text.RegularExpressions;

namespace Companion.Core.Services;

/// <summary>
/// The authoritative producer of explicit frame requests: message text in, a typed
/// <see cref="FrameLifecycle.Request"/> out.
///
/// Deliberately NOT an <c>IntentKind</c>. Adding roleplay intents to the intent parser would
/// change what <c>Agent.HandleAsync</c> routes, and production routing must not move; this is
/// read only by the shadow path. It is the same closed-pattern shape the intent parser already
/// uses — narrow, deterministic, and declining when unsure.
///
/// **It keys on FRAMING VERBS, never on content.** There is no pattern here for sexual,
/// romantic, profane, dark or violent language, and there must not be: those are ordinary
/// possible fictional content, and content never activates a frame, never restricts one, and
/// never exits one. "Let's roleplay" enters a frame whatever the scene is about; a graphic
/// message with no framing request enters nothing.
///
/// The three rules it serves, from <see cref="FrameLifecycle"/>:
///  - entering needs an explicit request; markup alone is a hint;
///  - exiting is generous, and anything exit-shaped resolves toward exit;
///  - anything unrecognised is <see cref="FrameLifecycle.Request.None"/>.
/// </summary>
public static partial class FrameRequestReader
{
    private const RegexOptions O =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // "stop", "out of character", "ooc", "end the roleplay", "drop the act", "be yourself".
    // Read FIRST: an exit inside a scene must never be mistaken for a fresh request.
    [GeneratedRegex(
        @"\b(out of character|ooc\b|break character|drop the (act|character)|"
        + @"(end|stop|quit|leave|exit)( the| this)? (roleplay|role-play|rp|scene|story|game)|"
        + @"stop (roleplaying|role-playing|pretending|playing)|"
        + @"be yourself again|back to (normal|reality|being you)|"
        + @"let'?s stop( there| now)?|that'?s enough( of that)?)", O)]
    private static partial Regex ExitRx();

    // A bare "stop" or "wait" mid-scene: might be an exit, might be dialogue. Rule 2 applies.
    [GeneratedRegex(@"^\s*(stop|wait|hold on|pause|enough)[.!]?\s*$", O)]
    private static partial Regex AmbiguousExitRx();

    // "let's roleplay", "pretend you're X", "play my X", "you are X and I am Y", "act as".
    [GeneratedRegex(
        @"\b(let'?s (do (some|a bit of|a)? ?)?(roleplay|role-play|rp\b|a scene|a story)|"
        + @"(want|fancy) to roleplay|"
        + @"roleplay (as|with|something)|role-play (as|with)|"
        + @"pretend (you'?re|you are|to be|that you)|"
        + @"act as (if you|my|the|a)\b|"
        + @"play (my|the|a) \w+|"
        + @"you'?re (playing|going to play|the) \w+ and i'?m\b|"
        + @"(start|begin)( a| the)? (scene|story|roleplay))", O)]
    private static partial Regex EnterRx();

    // "switch to X", "now play Y", "change character", "new scene".
    [GeneratedRegex(
        @"\b(switch (to|character|scenes?)|now play (my|the|a)\b|"
        + @"change (character|scenes?)|new scene|different character|"
        + @"swap (character|to))", O)]
    private static partial Regex SwitchRx();

    // Classic roleplay action markup: *hugs you*, *sets down the lantern*. A HINT only.
    [GeneratedRegex(@"\*[^*\n]{2,80}\*", RegexOptions.Compiled)]
    private static partial Regex MarkupRx();

    /// <summary>
    /// Reads one user message. Order is deliberate: exits before entries, because "let's stop
    /// this scene" contains scene-words and must not read as a request to start one.
    /// </summary>
    public static FrameLifecycle.Request Read(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0)
            return FrameLifecycle.Request.None;

        if (ExitRx().IsMatch(text))
            return FrameLifecycle.Request.ExplicitExit;

        if (AmbiguousExitRx().IsMatch(text))
            return FrameLifecycle.Request.AmbiguousExit;

        if (SwitchRx().IsMatch(text))
            return FrameLifecycle.Request.ExplicitSwitch;

        if (EnterRx().IsMatch(text))
            return FrameLifecycle.Request.ExplicitEnter;

        // Markup is evidence about the SHAPE of a message, not a declaration that a story has
        // begun. The lifecycle turns this into nothing when no frame is active.
        if (MarkupRx().IsMatch(text))
            return FrameLifecycle.Request.DetectedInCharacter;

        return FrameLifecycle.Request.None;
    }
}
