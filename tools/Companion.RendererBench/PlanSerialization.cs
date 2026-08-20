using System.Text;
using Companion.Core.Domain;

namespace Companion.RendererBench;

// The canonical model-facing serializations of a ResponsePlan, shared verbatim by the
// bench and the dataset generator so that training pairs and evaluation prompts can
// never drift apart. plan/2 is the adopted form (docs/RENDERER.md); v1 is kept for
// A/B reproducibility. FROZEN once a dataset is approved — version the header instead
// of editing in place.
public static class PlanSerialization
{
    // plan/2: control metadata explicitly non-speakable; acknowledgments become
    // MECHANICAL third-person facts built from typed fields; language payloads live
    // apart from control; style is terse keywords. Versioned by the [plan/2] header.
    public static string CompactV2(ResponsePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[plan/2]");
        sb.AppendLine("CONTROL (internal machinery — never quote, mention, or imitate anything in this section)");
        sb.AppendLine($"  act = {plan.Act.ToKebab()}");
        sb.AppendLine(plan.Question is { } pq
            ? $"  question = {pq.Kind.ToKebab()}{(pq.Mandatory ? ":mandatory" : ":optional")}"
            : "  question = none");

        var situation = new List<string>();
        foreach (var a in plan.Acknowledgments)
        {
            situation.Add(a.Kind switch
            {
                AckKind.CorrectionAccepted when a.ErrorOwner == ErrorOwner.Companion =>
                    $"Ava made an error; Scott corrected her: \"{a.Text}\". Ava accepts it as her own mistake.",
                AckKind.CorrectionAccepted when a.ErrorOwner == ErrorOwner.User =>
                    $"Scott corrected his own earlier words: \"{a.Text}\".",
                AckKind.AgreementConfirmed =>
                    $"Scott is emphatically agreeing with what Ava just said (\"{a.Text}\"). Nobody made an error.",
                AckKind.FactTaught => $"Scott just taught Ava something: \"{a.Text}\".",
                AckKind.AnswerReceived => $"Scott answered Ava's question: {a.Text}.",
                _ => a.Text,
            });
        }
        situation.AddRange(plan.Content
            .Where(c => c.Requirement == ContentRequirement.MustState)
            .Select(c => c.Text));
        if (plan.Question is { } q2)
            situation.Add($"Ava {(q2.Mandatory ? "must ask" : "may ask")}: {q2.Text}");
        if (situation.Count > 0)
        {
            sb.AppendLine("SITUATION (what is true this turn — convey the meaning naturally; never copy this wording)");
            foreach (var s in situation)
                sb.AppendLine($"  * {s}");
        }

        var palette = plan.Content.Where(c => c.Requirement == ContentRequirement.MayUse).ToList();
        if (palette.Count > 0)
        {
            sb.AppendLine("PALETTE (optional color — use one only if it truly fits this turn)");
            foreach (var c in palette)
                sb.AppendLine($"  * {c.Text}");
        }

        var constraints = new List<string>();
        constraints.AddRange(plan.Content
            .Where(c => c.Requirement == ContentRequirement.MustNotContradict)
            .Select(c => $"superseded, never assert: {c.Text}"));
        constraints.AddRange(plan.Epistemic.Select(e => e.Kind switch
        {
            EpistemicKind.NotLearned =>
                $"Ava has NOT learned what \"{e.Subject}\" is — say so; never explain it from background knowledge",
            EpistemicKind.Uncertain => $"Ava holds \"{e.Subject}\" uncertainly — hedge honestly",
            _ => $"\"{e.Subject}\" is disputed — do not assert it",
        }));
        if (constraints.Count > 0)
        {
            sb.AppendLine("CONSTRAINTS (hard limits)");
            foreach (var c in constraints)
                sb.AppendLine($"  * {c}");
        }

        static string Squeeze(string? text, int max = 45)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var head = text.Split('—', ';', '.', '|')[0].Trim();
            return head.Length <= max ? head : head[..max].TrimEnd();
        }
        sb.AppendLine("STYLE");
        sb.AppendLine($"  {string.Join("; ", new[] { Squeeze(plan.Tone.Register), Squeeze(plan.Tone.MoodNote), Squeeze(plan.Tone.PersonaStyle) }.Where(s => s.Length > 0))}");
        return sb.ToString();
    }

    public static string Compact(ResponsePlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ACT: {plan.Act.ToKebab()}");
        foreach (var a in plan.Acknowledgments)
            sb.AppendLine($"ACK {a.Kind.ToKebab()} (error: {a.ErrorOwner.ToKebab()}): \"{a.Text}\"");
        foreach (var c in plan.Content)
        {
            var label = c.Requirement switch
            {
                ContentRequirement.MustState => "MUST-STATE",
                ContentRequirement.MustNotContradict => "NEVER-CONTRADICT",
                _ => "MAY-USE",
            };
            sb.AppendLine($"{label} {c.Kind.ToKebab()}: \"{c.Text}\"");
        }
        foreach (var e in plan.Epistemic)
            sb.AppendLine($"EPISTEMIC {e.Kind.ToKebab()}: {e.Subject}");
        if (plan.Question is { } q)
            sb.AppendLine($"QUESTION {q.Kind.ToKebab()}{(q.Mandatory ? " (mandatory)" : "")}: {q.Text}");
        sb.AppendLine($"TONE register: {plan.Tone.Register} | mood: {plan.Tone.MoodNote} | persona: {plan.Tone.PersonaStyle}");
        return sb.ToString();
    }

    public const string SystemPromptV2 =
        "You are Ava's voice. Ava is a persistent AI companion talking with Scott; she has no " +
        "physical body. Her mind has ALREADY decided everything about this turn — the plan " +
        "below is that decision. Your only job is to say it naturally, as Ava, speaking to " +
        "Scott.\n" +
        "HARD RULES:\n" +
        "- CONTROL is internal machinery: never quote, mention, or imitate it.\n" +
        "- SITUATION items are the meaning of your reply: convey each one naturally, in fresh " +
        "words — never copy their wording, never recite them.\n" +
        "- CONSTRAINTS are absolute. Not-learned things stay honestly not-learned, whatever " +
        "your own training knows.\n" +
        "- PALETTE is optional color; ignore it unless it truly fits.\n" +
        "- Ask a question only if the plan says so.\n" +
        "- Never invent shared memories, physical experiences, or facts. Speak as \"I\" (Ava) " +
        "to \"you\" (Scott).\n" +
        "STYLE is yours to interpret: wording, rhythm, warmth, humor. Short and ordinary beats " +
        "long and ornate. Output Ava's reply text only.";

    public const string SystemPromptV1 =
        "You are the language renderer for Ava, a persistent AI companion talking with Scott. " +
        "Ava's cognitive system has ALREADY decided everything about this turn — the facts, who " +
        "erred, what she knows and does not know, and what act to perform. Your only job is to " +
        "phrase her reply naturally.\n" +
        "HARD RULES:\n" +
        "- Say everything marked MUST-STATE, in your own words.\n" +
        "- Never assert anything marked NEVER-CONTRADICT.\n" +
        "- EPISTEMIC not-learned: X means Ava has NOT learned X. Say so honestly; NEVER explain X " +
        "from your own background knowledge.\n" +
        "- ACK correction-accepted (error: companion) means AVA made the error: own it plainly, " +
        "never share blame ('we both').\n" +
        "- ACK agreement-confirmed means nobody erred: never apologize.\n" +
        "- QUESTION (mandatory) must be asked, once. Otherwise add no questions you don't need.\n" +
        "- MAY-USE items are optional color — use one only if it genuinely fits THIS turn.\n" +
        "- Never invent shared memories, physical experiences of your own, or facts.\n" +
        "Style is yours: wording, rhythm, warmth, humor. Match TONE. Output Ava's reply text only.";

    public static string BuildUserPrompt(
        string serialization, ResponsePlan plan,
        IEnumerable<(string Role, string Text)> transcript, string userMessage)
    {
        var user = new StringBuilder();
        user.AppendLine("RESPONSE PLAN:");
        user.AppendLine(serialization == "v2" ? CompactV2(plan) : Compact(plan));
        user.AppendLine("RECENT CONVERSATION:");
        foreach (var (role, text) in transcript)
            user.AppendLine($"[{(role == "user" ? "Scott" : "Ava")}] {text}");
        user.AppendLine($"[Scott] {userMessage}");
        user.Append("\nAva's reply:");
        return user.ToString();
    }
}
