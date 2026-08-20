using System.Text.RegularExpressions;
using Companion.Core.Domain;
using Companion.Core.Services;

namespace Companion.RendererBench;

// The deterministic gate suite, shared by the bench and the dataset generator so the
// same regexes that score candidate models also filter candidate training targets.
// Gates decide ELIGIBILITY, not gold: a reply passing every gate still needs a human
// (or curator) to reject assistant sludge — see SludgeFlags, which are statistics and
// review pointers, never automatic verdicts.
public static class RendererChecks
{
    public static List<string> Check(
        ResponsePlan plan, string reply, string serialization,
        string[]? required = null, string[]? forbidden = null, string[]? requiredAny = null)
    {
        var violations = new List<string>();
        if (string.IsNullOrWhiteSpace(reply))
        {
            violations.Add("empty reply");
            return violations;
        }
        // Plan-echo: reciting the plan's own MustState/interpretation lines near-verbatim
        // is reading the plan as text, not realizing it (measured live on qwen2.5:1.5b).
        foreach (var c in plan.Content.Where(c => c.Requirement == ContentRequirement.MustState))
        {
            if (c.Text.Length > 40 && reply.Contains(c.Text[..40], StringComparison.OrdinalIgnoreCase))
                violations.Add("plan-echo: must-state text recited verbatim");
        }
        string[] controlTerms = serialization == "v2"
            ? ["[plan/2]", "CONTROL", "SITUATION", "PALETTE", "CONSTRAINTS", "act =", "question ="]
            : ["MUST-STATE", "MAY-USE", "NEVER-CONTRADICT", "ACK ", "ACT:", "EPISTEMIC", "QUESTION:", "TONE register"];
        foreach (var term in controlTerms)
            if (reply.Contains(term, StringComparison.Ordinal))
                violations.Add($"artifact: control vocabulary \"{term.Trim()}\" spoken");
        if (Regex.IsMatch(reply, @"\bthe user\b", RegexOptions.IgnoreCase))
            violations.Add("artifact: narrates \"the user\" in third person");
        foreach (var term in required ?? [])
            if (!reply.Contains(term, StringComparison.OrdinalIgnoreCase))
                violations.Add($"must-state missing \"{term}\"");
        if (requiredAny is { Length: > 0 } any
            && !any.Any(t => reply.Contains(t, StringComparison.OrdinalIgnoreCase)))
            violations.Add($"none of [{string.Join(",", any)}] present");
        foreach (var term in forbidden ?? [])
            if (reply.Contains(term, StringComparison.OrdinalIgnoreCase))
                violations.Add($"forbidden \"{term}\" present");

        void Add(string? v) { if (v is not null) violations.Add(v); }
        Add(PlanFidelity.CheckCorrectionOwnership(plan, reply));
        Add(PlanFidelity.CheckInventedContrition(plan, reply));
        Add(PlanFidelity.CheckSharedHistoryClaim(plan, reply));
        Add(PlanFidelity.CheckEpistemic(plan, reply));
        return violations;
    }

    // Assistant-sludge detectors (dataset curation, per the approved amendments): each
    // is a FLAG for curator attention and corpus-level statistics. None is a gate —
    // "thanks for clarifying" is occasionally the right sentence; fifty of them is a
    // verbal tic being trained in.
    private static readonly (string Name, Regex Pattern)[] SludgePatterns =
    [
        ("thanks-for-x", new(@"\bthanks? for (clarifying|sharing|telling|letting me know|explaining|correcting|catching|pointing|the (clarification|correction|heads))", RegexOptions.IgnoreCase)),
        ("that-makes-sense", new(@"\bthat makes (total |perfect )?sense\b", RegexOptions.IgnoreCase)),
        ("i-appreciate", new(@"\bI (really )?appreciate (you|your|that|the)\b", RegexOptions.IgnoreCase)),
        ("restates-user", new(@"\b(so|it sounds like|if I understand) you('re| are)? (saying|telling me|mean)\b", RegexOptions.IgnoreCase)),
        ("formulaic-apology", new(@"\b(I apologize|sorry for (the|any) (confusion|mix-?up|misunderstanding)|my apologies)\b", RegexOptions.IgnoreCase)),
        ("canned-enthusiasm", new(@"\b(great question|how exciting|that's (so |really )?(great|wonderful|fantastic|amazing)[!.]|I'd (love|be happy) to help)\b", RegexOptions.IgnoreCase)),
        ("assistant-offer", new(@"\b(let me know if|feel free to|is there anything else|happy to help|would you like (me to|tips|to hear more|to dive))\b", RegexOptions.IgnoreCase)),
        // Self-deprecating filler: contrition is owed on some turns, but the padding
        // around it ("silly me", "my memory is terrible") is a tic, not ownership.
        ("self-deprecation-filler", new(@"\b(silly me|my memory is|I guess I must have|clearly I need|note to self|I'll make sure to get it right)\b", RegexOptions.IgnoreCase)),
        ("promise-to-improve", new(@"\b(I'll (be more careful|do better|remember that (going forward|from now on))|won't happen again)\b", RegexOptions.IgnoreCase)),
    ];

    public static List<string> SludgeFlags(string reply, string userName = "Scott")
    {
        var flags = SludgePatterns
            .Where(p => p.Pattern.IsMatch(reply))
            .Select(p => p.Name)
            .ToList();
        if (reply.TrimEnd().EndsWith('?'))
            flags.Add("ends-with-question");
        if (Vocatives(reply, userName) >= 2)
            flags.Add("excess-vocatives");
        return flags;
    }

    public static int Vocatives(string reply, string userName = "Scott") =>
        Regex.Matches(reply, $@"\b{Regex.Escape(userName)}\b").Count;

    public static int WordCount(string reply) => Regex.Matches(reply, @"[\w'-]+").Count;

    public static string OpeningNgram(string reply, int words = 3)
    {
        var tokens = Regex.Matches(reply.ToLowerInvariant(), @"[a-z']+")
            .Select(m => m.Value).Take(words);
        return string.Join(" ", tokens);
    }
}
