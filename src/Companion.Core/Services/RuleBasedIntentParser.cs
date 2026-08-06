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
        return TryPersona(text)
            ?? TryStyle(text)
            ?? TryFeedback(text)
            ?? TryPrivacy(text)
            ?? TryDispute(text)
            ?? TryForget(text)
            ?? TryRecall(text)
            ?? TryLists(text)
            ?? TryConsolidate(text)
            ?? Intent.Chat;
    }

    private static readonly Regex PrivacyRx = new(
        @"\b(do ?n'?t (?:remember|save|store|record|log) this|" +
        @"forget this (?:conversation|chat|session|exchange)|" +
        @"(?:this is|keep this|make this|let'?s keep this) (?:private|off[- ]the[- ]record)|" +
        @"private (?:session|mode)|off[- ]the[- ]record|don'?t keep this)\b", Opts);

    private static Intent? TryPrivacy(string text)
        => PrivacyRx.IsMatch(text) ? new Intent { Kind = IntentKind.PrivacyDoNotRemember, Argument = Clean(text) } : null;

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

    private static readonly Regex ConsolidateRx = new(@"\bconsolidate\b.*\bmemor|\bconsolidate your memor", Opts);

    private static Intent? TryConsolidate(string text)
        => ConsolidateRx.IsMatch(text) || text.Equals("consolidate", StringComparison.OrdinalIgnoreCase)
            ? new Intent { Kind = IntentKind.Consolidate }
            : null;

    private static string Clean(string value)
        => value.Trim().TrimEnd('.', '!', '?', ',', ';').Trim();
}
