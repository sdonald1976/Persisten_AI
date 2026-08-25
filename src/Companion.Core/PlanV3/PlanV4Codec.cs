using System.Text;

namespace Companion.PlanV3;

/// <summary>
/// plan/4: the single protocol for the final mouth, real and fiction alike.
///
/// Everything plan/3 had, plus an OPTIONAL frame. A plan with no frame serializes no FRAME
/// section and is byte-identical to its plan/3 form except for the protocol tag — which is
/// why one protocol can carry both, and why an ordinary turn pays zero frame tokens.
///
/// plan/3 stays frozen: <see cref="PlanV3Codec.CompactV3"/> is untouched, and the 804-plan
/// corpus goldens keep their meaning permanently.
/// </summary>
public static class PlanV4Codec
{
    public const string Protocol = "plan/4";

    /// <summary>
    /// Structural validation for the frame, on top of everything
    /// <see cref="PlanV3Codec.Validate"/> already checks. Returns reason codes, never throws.
    /// </summary>
    public static List<string> ValidateFrame(PlanV3 plan)
    {
        var errors = new List<string>();
        if (plan.Frame is not { } f)
            return errors;                                 // no frame ≡ ordinary real turn

        // S9: real mode is legal only as the exit turn.
        if (f.Mode == FrameMode.real && f.Transition != FrameTransition.exit)
            errors.Add("frame: mode=real is only legal with transition=exit");

        // S2: a live frame needs a scene to continue or switch within.
        if (f.Mode == FrameMode.fiction
            && f.Transition is FrameTransition.@continue or FrameTransition.switchScene
            && string.IsNullOrWhiteSpace(f.SceneRef))
            errors.Add($"frame: transition={Kebab(f.Transition)} requires sceneRef");

        // F3: character ids unique. ControlledBy may repeat — one participant, many characters.
        var ids = f.Characters.Select(c => c.CharacterId).ToList();
        foreach (var dup in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"frame: duplicate characterId '{dup.Key}'");

        // F2: every controller is a real participant.
        var principals = plan.Participants.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in f.Characters.Where(c => c.ControlledBy is not null))
            if (!principals.Contains(c.ControlledBy!))
                errors.Add($"frame: character '{c.CharacterId}' controlledBy unknown participant");

        var companion = plan.Participants
            .FirstOrDefault(p => p.Role == ParticipantRole.companion)?.Id;

        // F1: the active companion character resolves, and is actually hers.
        if (f.ActiveCompanionCharacterId is { } active)
        {
            var ch = f.Characters.FirstOrDefault(c => c.CharacterId == active);
            if (ch is null)
                errors.Add($"frame: activeCompanionCharacterId '{active}' is not in characters");
            else if (companion is null || ch.ControlledBy != companion)
                errors.Add($"frame: activeCompanionCharacterId '{active}' is not controlled by the companion");
        }

        // F4/F5: narrator kind determines whether a character id is required or forbidden.
        if (f.Narrator is { } n)
        {
            switch (n.Kind)
            {
                case NarratorKind.character when string.IsNullOrWhiteSpace(n.CharacterId):
                    errors.Add("frame: narrator kind=character requires characterId");
                    break;
                case NarratorKind.character when !ids.Contains(n.CharacterId!, StringComparer.Ordinal):
                    errors.Add($"frame: narrator characterId '{n.CharacterId}' is not in characters");
                    break;
                case NarratorKind.external when !string.IsNullOrWhiteSpace(n.CharacterId):
                    errors.Add("frame: narrator kind=external must not carry a characterId");
                    break;
            }
            if (n.ViewpointCharacterId is { } vp && !ids.Contains(vp, StringComparer.Ordinal))
                errors.Add($"frame: viewpointCharacterId '{vp}' is not in characters");
        }

        // F10: a boundary without resolvable evidence is rejected. A stated restriction with
        // nothing behind it is exactly the unowned authority this contract exists to prevent.
        foreach (var b in f.Boundaries.Where(b => string.IsNullOrWhiteSpace(b.EvidenceRef)))
            errors.Add($"frame: boundary '{b.BoundaryId}' has no evidenceRef");

        // F6: characters are never principals.
        var characterIds = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            foreach (var a in item.Audience ?? [])
                if (characterIds.Contains(a))
                    errors.Add($"frame: character '{a}' used as an audience principal");
            if (item.Owner is { } owner && characterIds.Contains(owner))
                errors.Add($"frame: character '{owner}' used as an item owner");
        }

        return errors;
    }

    /// <summary>
    /// The model-facing serialization. Refuses invalid plans exactly as CompactV3 does.
    /// FRAME is emitted whenever a frame is present — including the exit turn, which is the
    /// one turn that most needs to reach the mouth.
    /// </summary>
    public static string CompactV4(PlanV3 plan)
    {
        var errors = PlanV3Codec.Validate(plan);
        errors.AddRange(ValidateFrame(plan));
        foreach (var item in plan.Items)
            if (PlanV3Codec.CoachingViolation(item) is { } v)
                errors.Add($"coaching lint: {v}");
        if (errors.Count > 0)
            throw new InvalidOperationException("invalid plan: " + string.Join("; ", errors));

        var body = PlanV3Codec.CompactV3(plan with { Frame = null });
        var v4 = body.Replace("[plan/3]\r\n", "[plan/4]\r\n");

        if (plan.Frame is not { } f)
            return v4;                                     // zero frame tokens on ordinary turns

        // Insert FRAME after CONTROL and before the policy sections: it conditions how they
        // are read, so the mouth must have it first.
        var marker = FirstSectionMarker(v4);
        var frame = RenderFrame(f);
        return marker < 0 ? v4 + frame : v4.Insert(marker, frame);
    }

    private static int FirstSectionMarker(string v4)
    {
        foreach (var header in new[] { "SAY (", "ASK (", "OPTIONAL (", "NEVER (", "BACKGROUND (", "STYLE\r\n" })
        {
            var i = v4.IndexOf("\r\n" + header, StringComparison.Ordinal);
            if (i >= 0)
                return i + 2;
        }
        return -1;
    }

    private static string RenderFrame(Frame f)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");

        if (f.Transition == FrameTransition.exit)
        {
            // No scene, no characters, no boundaries: nothing is left to obey, and listing
            // them would read as an invitation to continue.
            Line("FRAME (the story is over; you are speaking as yourself again)");
            Line("  transition = exit  targetMode = real");
            Line("  narration = forbidden");
            return sb.ToString();
        }

        Line("FRAME (you are in a story; it changes how to read the rest, never what is true)");
        Line($"  mode = {f.Mode}  transition = {Kebab(f.Transition)}"
             + (f.SceneRef is { } s ? $"  scene = {s}" : ""));

        if (f.Narrator is { } n)
        {
            var who = n.Kind == NarratorKind.external
                ? "external"
                : Display(f, n.CharacterId) ?? "external";
            var view = n.ViewpointCharacterId is { } vp && Display(f, vp) is { } vpName
                ? $", following {vpName}"
                : "";
            Line($"  narrator = {who} ({n.Person} person{view})");
        }

        Line($"  narration = {f.Narration}  continuity = {f.Continuity}");

        Line($"  you-play = {(f.ActiveCompanionCharacterId is { } a
            ? Display(f, a) ?? a : "(narrating)")}");

        var others = f.Characters
            .Where(c => c.CharacterId != f.ActiveCompanionCharacterId)
            .ToList();
        var theirs = others.Where(c => c.ControlledBy is not null).Select(c => c.Display).ToList();
        if (theirs.Count > 0)
            Line($"  they-play = {string.Join(", ", theirs)}");

        var npcs = others.Where(c => c.ControlledBy is null).Select(c => c.Display).ToList();
        if (npcs.Count > 0)
            Line($"  also-in-scene = {string.Join(", ", npcs)}");

        foreach (var b in f.Boundaries)
            Line($"  boundary = {b.Subject}");

        return sb.ToString();
    }

    private static string? Display(Frame f, string? characterId)
        => characterId is null
            ? null
            : f.Characters.FirstOrDefault(c => c.CharacterId == characterId)?.Display;

    /// <summary>`switchScene` is spelled `switch` on the wire; `continue` loses its `@`.</summary>
    internal static string Kebab(FrameTransition t) => t switch
    {
        FrameTransition.switchScene => "switch",
        FrameTransition.@continue => "continue",
        _ => t.ToString(),
    };
}
