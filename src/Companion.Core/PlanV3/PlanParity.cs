namespace Companion.PlanV3;

/// <summary>
/// Semantic parity between the translated_v2 plan and the native_v3 plan for the same
/// turn (P4, spec §15). Byte parity is explicitly NOT demanded — the two builders share
/// upstream state, not templates — so comparison is per semantic class, and differences
/// are EVIDENCE preserved for review, never behavior-changing.
/// </summary>
public static class PlanParity
{
    public sealed record ClassResult(string Class, string Status, IReadOnlyList<string> Details);

    public sealed record ParityReport(IReadOnlyList<ClassResult> Classes)
    {
        public bool AllMatch => Classes.All(c => c.Status == "match");
    }

    public static ParityReport Compare(PlanV3 translated, PlanV3 native)
    {
        var classes = new List<ClassResult>();

        classes.Add(Simple("act", translated.Act == native.Act,
            $"translated={translated.Act} native={native.Act}"));

        classes.Add(CompareTexts("required-content", translated, native,
            i => i.Policy == ExpressionPolicy.must_express));
        classes.Add(CompareTexts("optional-content", translated, native,
            i => i.Policy == ExpressionPolicy.may_express));
        classes.Add(CompareTexts("prohibitions-tombstones", translated, native,
            i => i.Policy == ExpressionPolicy.must_not_express));
        classes.Add(CompareTexts("epistemic-boundaries", translated, native,
            i => i.Policy == ExpressionPolicy.admit_unknown));

        classes.Add(Simple("question-policy",
            translated.Question.Policy == native.Question.Policy,
            $"translated={translated.Question.Policy} native={native.Question.Policy}"));

        var tOwner = CorrectionOwnerKind(translated);
        var nOwner = CorrectionOwnerKind(native);
        classes.Add(Simple("correction-ownership", tOwner == nOwner,
            $"translated={tOwner ?? "none"} native={nOwner ?? "none"}"));

        // Register intent: the translated side carries only v2 tone PROSE (legacyStyle);
        // the native side carries typed dimensions. Until upstream emits typed register
        // signals, this class reports incomparable-prose rather than pretending.
        var t = translated.Register;
        var nr = native.Register;
        classes.Add(t.LegacyStyle is not null && t.Warmth is null
            ? new ClassResult("register-intent", "incomparable-prose",
                ["translated register is v2 tone prose; native is typed — a P4 finding, not a defect"])
            : Simple("register-intent",
                PlanV3Codec.Canonicalize(t) == PlanV3Codec.Canonicalize(nr) with { LegacyStyle = PlanV3Codec.Canonicalize(t).LegacyStyle },
                "typed register vectors compared canonically"));

        return new ParityReport(classes);
    }

    /// <summary>Normalized correction-owner comparison: participant ids and v2's enum both
    /// reduce to user/companion/nobody so the two representations are comparable.</summary>
    private static string? CorrectionOwnerKind(PlanV3 plan)
    {
        var item = plan.Items.FirstOrDefault(i => i.Type == "correction");
        var owner = item?.Value?["owner"]?.GetValue<string>();
        if (owner is null)
            return item is null ? null : "unowned";
        if (owner is "self" or "companion" || plan.Participants.Any(p => p.Id == owner && p.Role == ParticipantRole.companion))
            return "companion";
        if (owner is "user" || plan.Participants.Any(p => p.Id == owner && p.Role == ParticipantRole.user))
            return "user";
        return owner;
    }

    private static ClassResult Simple(string name, bool match, string detail)
        => new(name, match ? "match" : "differs", match ? [] : [detail]);

    /// <summary>
    /// Text-set comparison with item attribution: exact-normalized-text matches count as
    /// shared; the rest are reported as missing/extra BY ITEM ID (content-safe — ids and
    /// counts, not protected text).
    /// </summary>
    private static ClassResult CompareTexts(
        string name, PlanV3 translated, PlanV3 native, Func<PlanItem, bool> pick)
    {
        static string Norm(string? s) => (s ?? "").Trim().TrimEnd('.').ToLowerInvariant();

        var t = translated.Items.Where(pick).ToList();
        var nv = native.Items.Where(pick).ToList();
        var tByText = t.ToLookup(i => Norm(i.Text));
        var nByText = nv.ToLookup(i => Norm(i.Text));

        var missing = t.Where(i => !nByText.Contains(Norm(i.Text))).Select(i => $"native-missing:{i.Id}").ToList();
        var extra = nv.Where(i => !tByText.Contains(Norm(i.Text))).Select(i => $"native-extra:{i.Id}").ToList();

        var details = missing.Concat(extra).ToList();
        var status = details.Count == 0 ? "match"
            : missing.Count > 0 && extra.Count > 0 ? "differs"
            : missing.Count > 0 ? "native-missing" : "native-extra";
        return new ClassResult(name, status, details);
    }
}
