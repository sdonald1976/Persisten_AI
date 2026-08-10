using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// Deterministic, offline intent parser: turns plain language into an <see cref="Intent"/> so the
/// user never needs slash commands. Anything it doesn't recognize is an ordinary
/// <see cref="IntentKind.Chat"/> turn, so it never hijacks normal conversation. An LLM
/// tool-calling parser can replace this behind <see cref="IIntentParser"/> later.
/// </summary>
public sealed class RuleBasedIntentParser : IIntentParser
{
    private static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Referents that mean "the thing we were just talking about".
    private static readonly HashSet<string> Referents = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "it", "this", "the last thing", "the last one", "what i said", "what i just said",
        "that memory", "the last memory",
    };

    private static readonly HashSet<string> StyleAdjectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "concise", "brief", "short", "terse", "detailed", "thorough", "verbose", "formal",
        "casual", "informal", "friendly", "warm", "funny", "humorous", "serious", "blunt",
        "direct", "polite", "professional", "playful", "sarcastic", "technical", "simple",
    };

    public Intent Parse(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return Intent.Chat;

        // Order matters: check specific, side-effecting phrasings before general chat.
        // Privacy runs before Forget so "forget this conversation" is a privacy toggle, not a
        // memory deletion.
        return TryIdentity(text)
            ?? TryPersonality(text)
            ?? TryPersona(text)
            ?? TryStyle(text)
            ?? TryFeedback(text)
            ?? TryPrivacy(text)
            ?? TryDispute(text)
            ?? TryForget(text)
            ?? TryRecall(text)
            ?? TryLists(text)
            ?? TryConsolidate(text)
            ?? TryThoughts(text)
            ?? TryGreeting(text)
            ?? Intent.Chat;
    }

    // A bare greeting only — "hi", "hello there", "good morning" — not a greeting that carries a
    // real request ("hi, can you help with X"), which should be handled as a normal turn.
    private static readonly Regex GreetingRx = new(
        @"^(hi|hii+|hey+|hello|heya|hiya|yo|sup|howdy|greetings|good (?:morning|afternoon|evening)|" +
        @"morning|hi there|hey there|hello there)[\s!.,]*$", Opts);

    private static Intent? TryGreeting(string text)
        => GreetingRx.IsMatch(text) ? new Intent { Kind = IntentKind.Greeting, Argument = null } : null;

    private static readonly Regex PrivacyRx = new(
        @"\b(do ?n'?t (?:remember|save|store|record|log) this|" +
        @"forget this (?:conversation|chat|session|exchange)|" +
        @"(?:this is|keep this|make this|let'?s keep this) (?:private|off[- ]the[- ]record)|" +
        @"private (?:session|mode)|off[- ]the[- ]record|don'?t keep this)\b", Opts);

    private static Intent? TryPrivacy(string text)
        => PrivacyRx.IsMatch(text) ? new Intent { Kind = IntentKind.PrivacyDoNotRemember, Argument = Clean(text) } : null;

    // The companion's identity: a name ("your name is Ava"), a gender ("you're a woman"), or
    // pronouns ("use she/her"). Routed as one intent; the Agent extracts whichever parts are present.
    private static readonly Regex IdentityRx = new(
        @"(?:your name is|call yourself|i'?ll call you|i'?ll name you|name yourself|you'?re called)\s+\S+" +
        @"|you(?:'?re| are)\s+(?:a\s+)?(?:woman|man|girl|guy|boy|female|male|non-?binary)\b" +
        @"|(?:your pronouns are|use)\s+[a-z]+/[a-z]+", Opts);

    private static Intent? TryIdentity(string text)
        => IdentityRx.IsMatch(text) ? new Intent { Kind = IntentKind.SetIdentity, Argument = Clean(text) } : null;

    // "set/switch/use ... personality to X", "use the X personality", or a request to see the options.
    private static readonly Regex PersonalitySetRx = new(
        @"^(?:set|change|switch(?:\s+to)?|use|give\s+yourself|make\s+yourself)\s+(?:your\s+|the\s+)?personality\s*(?:to|as|:|=)?\s*(.+)$", Opts);
    private static readonly Regex PersonalityUseRx = new(
        @"^(?:use|switch\s+to|be|become)\s+(?:the\s+)?([a-z][\w &-]*?)\s+personality\b", Opts);
    private static readonly Regex PersonalityAskRx = new(
        @"(?:what|which|list|show|available).*\bpersonalit(?:y|ies)\b|\bpersonalit(?:y|ies)\b.*\b(?:options|available|choices|are there)\b", Opts);

    private static Intent? TryPersonality(string text)
    {
        var set = PersonalitySetRx.Match(text);
        if (set.Success)
            return new Intent { Kind = IntentKind.SetPersonality, Argument = Clean(set.Groups[1].Value) };

        var use = PersonalityUseRx.Match(text);
        if (use.Success)
            return new Intent { Kind = IntentKind.SetPersonality, Argument = Clean(use.Groups[1].Value) };

        // A request to see the options (no specific choice) — Argument stays null.
        if (PersonalityAskRx.IsMatch(text))
            return new Intent { Kind = IntentKind.SetPersonality, Argument = null };

        return null;
    }

    private static readonly Regex PersonaRx =
        new(@"^(?:set\s+)?(?:your\s+)?persona\s*(?:to|as|:|is)?\s*(.+)$", Opts);

    private static Intent? TryPersona(string text)
    {
        var m = PersonaRx.Match(text);
        return m.Success
            ? new Intent { Kind = IntentKind.SetPersona, Argument = Clean(m.Groups[1].Value) }
            : null;
    }

    private static readonly Regex BeMoreLessRx = new(@"^(?:please\s+)?be\s+(?:more|less)\s+.+", Opts);
    private static readonly Regex BeStyleRx = new(@"^(?:please\s+)?be\s+(\w+)", Opts);
    private static readonly Regex TalkLikeRx = new(@"^(?:talk|speak|write|respond|reply)\s+(?:like|in|with)\s+.+", Opts);
    private static readonly Regex FromNowRx = new(@"^from now on[,\s]+.+", Opts);
    private static readonly Regex KeepItRx = new(@"^keep (?:it|your (?:answers|replies|responses))\s+.+", Opts);

    private static Intent? TryStyle(string text)
    {
        var isStyle =
            BeMoreLessRx.IsMatch(text) ||
            TalkLikeRx.IsMatch(text) ||
            FromNowRx.IsMatch(text) ||
            KeepItRx.IsMatch(text) ||
            (BeStyleRx.Match(text) is { Success: true } bm && StyleAdjectives.Contains(bm.Groups[1].Value));

        return isStyle ? new Intent { Kind = IntentKind.AdjustStyle, Argument = Clean(text) } : null;
    }

    private static readonly Regex PosRx = new(
        @"\b(that was (?:great|perfect|awesome|helpful|excellent|spot on|exactly right)|" +
        @"perfect|nailed it|well done|good (?:answer|reply|response)|exactly right|much better)\b", Opts);
    private static readonly Regex NegRx = new(
        @"\b(that was (?:unhelpful|bad|terrible|useless|off|wrong)|not helpful|" +
        @"bad (?:answer|reply|response)|that (?:didn'?t|did not) help|wrong answer|" +
        @"that'?s not what i (?:meant|asked)|unhelpful)\b", Opts);

    private static Intent? TryFeedback(string text)
    {
        if (PosRx.IsMatch(text)) return new Intent { Kind = IntentKind.FeedbackPositive, Argument = text };
        if (NegRx.IsMatch(text)) return new Intent { Kind = IntentKind.FeedbackNegative, Argument = text };
        return null;
    }

    // A disputed FACT (not reply quality): "that's wrong / not right / incorrect / false".
    private static readonly Regex DisputeRx = new(
        @"^(?:no,?\s+)?that'?s (?:wrong|not right|incorrect|false|not correct)\b", Opts);

    private static Intent? TryDispute(string text)
        => DisputeRx.IsMatch(text) ? new Intent { Kind = IntentKind.Dispute } : null;

    private static readonly Regex ForgetRx = new(@"^forget\s+(?:about\s+)?(.*)$", Opts);
    private static readonly Regex DeleteMemRx =
        new(@"^delete\s+(?:that memory|what i (?:said|told you)|the last (?:thing|memory))\b", Opts);

    private static Intent? TryForget(string text)
    {
        if (DeleteMemRx.IsMatch(text))
            return new Intent { Kind = IntentKind.Forget, Argument = null };

        var m = ForgetRx.Match(text);
        if (!m.Success)
            return null;

        var rest = Clean(m.Groups[1].Value);
        if (string.IsNullOrEmpty(rest) || Referents.Contains(rest))
            return new Intent { Kind = IntentKind.Forget, Argument = null };

        // "forget what I said about X" -> target the topic X.
        var about = Regex.Match(rest, @"\babout\s+(.+)$", Opts);
        var target = about.Success ? Clean(about.Groups[1].Value) : rest;
        return new Intent { Kind = IntentKind.Forget, Argument = target };
    }

    private static readonly Regex RecallRx = new(
        @"^(?:what do you (?:remember|know)|tell me what you (?:remember|know)|" +
        @"show me what you (?:remember|know))(?:\s+about\s+(.+))?", Opts);

    private static Intent? TryRecall(string text)
    {
        var m = RecallRx.Match(text);
        if (!m.Success)
            return null;

        var topic = Clean(m.Groups[1].Value);
        if (string.IsNullOrEmpty(topic) || topic.Equals("me", StringComparison.OrdinalIgnoreCase))
            return new Intent { Kind = IntentKind.Recall, Argument = null };
        return new Intent { Kind = IntentKind.Recall, Argument = topic };
    }

    private static readonly Regex LoopsRx = new(
        @"open loops?\b|^what'?s unfinished|^what do i (?:still )?need to|loose ends|" +
        @"^what am i waiting on\b", Opts);

    private static Intent? TryLists(string text)
    {
        // Open loops first (more specific), then projects.
        if (LoopsRx.IsMatch(text)) return new Intent { Kind = IntentKind.ListOpenLoops };
        if (Regex.IsMatch(text, @"^(?:list|show)\s+(?:my\s+|all\s+)?projects\b", Opts) ||
            Regex.IsMatch(text, @"^what (?:projects )?am i working on\b", Opts) ||
            Regex.IsMatch(text, @"^what are my projects\b", Opts))
            return new Intent { Kind = IntentKind.ListProjects };
        return null;
    }

    // Asking about the companion's OWN thoughts — "what's on your mind", "what are you thinking
    // about?", "penny for your thoughts". Deliberately narrow and mostly end-anchored so an opinion
    // request ("what do you think about my plan?") or a thought about a named subject ("what are
    // you thinking about the move?") stays an ordinary chat turn.
    private static readonly Regex ThoughtsRx = new(
        @"^(?:so,?\s+)?(?:" +
        @"what(?:'?s| is| has| have)? (?:been )?on your mind" +
        @"|what (?:are|were) you thinking(?: about)?" +
        @"|what (?:have|'?ve) you been thinking(?: about)?" +
        @"|penny for your thoughts" +
        @"|anything on your mind" +
        @"|(?:tell me )?what you'?ve been thinking(?: about)?" +
        @")[\s?!.,]*$", Opts);

    private static Intent? TryThoughts(string text)
        => ThoughtsRx.IsMatch(text) ? new Intent { Kind = IntentKind.ShareThoughts } : null;

    private static readonly Regex ConsolidateRx = new(@"\bconsolidate\b.*\bmemor|\bconsolidate your memor", Opts);

    private static Intent? TryConsolidate(string text)
        => ConsolidateRx.IsMatch(text) || text.Equals("consolidate", StringComparison.OrdinalIgnoreCase)
            ? new Intent { Kind = IntentKind.Consolidate }
            : null;

    private static string Clean(string value)
        => value.Trim().TrimEnd('.', '!', '?', ',', ';').Trim();
}
