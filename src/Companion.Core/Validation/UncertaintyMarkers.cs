using System.Text.RegularExpressions;

namespace Companion.Core.Validation;

/// <summary>
/// Does a reply actually NAME a gap in what is known?
///
/// This is the canonical implementation, and it lives in Core because two of them would
/// eventually disagree and the disagreement would look like a model result. The corpus factory
/// gates training rows with it; the renderer decides a canary fallback with it. One instrument,
/// one answer.
///
/// Two bugs are baked out of it by construction, both of which cost real time:
///
/// 1. <b>Typographic apostrophes.</b> The model writes "I don’t know", not "I don't know". An
///    ASCII-only pattern scored 167 of 181 good rows as having dropped the uncertainty. The
///    check was wrong, not the corpus. <see cref="Normalise"/> folds the punctuation first.
/// 2. <b>Literal backspace.</b> In C# "\b" inside a NON-verbatim string is U+0008, not a word
///    boundary — the same mistake that, in a Python source file, once scored 181 of 181 rows as
///    failures. Every pattern here is a verbatim (@) string, and the test suite asserts the
///    compiled pattern contains no control characters so a later edit cannot reintroduce it.
///
/// Deliberately requires a marker that NAMES the gap. "we'll have to wait and see" is not an
/// admission — it is the deferral the Run-2.1 supplement exists to unteach, and it stays a
/// failure.
/// </summary>
public static class UncertaintyMarkers
{
    private static readonly Regex UncertaintyMarker = new(
        @"\b(?:"
        + @"do(?:n't| not) know|did(?:n't| not) (?:know|see|catch)|no idea|not sure|unsure"
        + @"|can(?:'t|not) (?:tell|say|confirm)|could(?:n't| not) (?:tell|say)"
        + @"|not clear|unclear|hard to say|no telling"
        + @"|no (?:word|update|updates|news|sign|detail|details|specifics)"
        + @"|nothing (?:yet|so far|back|from|more|further)"
        + @"|(?:don't|do not|haven't|have not) have (?:any |the )?(?:update|updates|detail|details|specifics|word)"
        + @"|yet to|still (?:waiting|open|unknown)|(?:just |still )?waiting (?:to hear|on|for)"
        + @"|have(?:n't| not) heard|hav(?:e|en't) (?:found out|been told)"
        + @"|was(?:n't| not) said|nobody (?:said|knows|has said)"
        + @"|one of them|either of them|which (?:one|of them)|not certain"
        + @"|open question|up in the air|to be (?:seen|confirmed)|remains to be seen"
        + @"|beyond that|past that|more than that"
        + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    /// <summary>
    /// The second family: a concept Ava was never taught, as opposed to an outcome not yet
    /// known. "I've never heard of zydeco" admits a gap without matching any pattern above,
    /// because the supplement's compositions are all about pending outcomes.
    ///
    /// These are separate on purpose. <see cref="Admits"/> is the instrument the Run-2 corpus
    /// was gated and frozen against and it does not move; the renderer's obligation is the
    /// wider one, so it takes the union. A superset with a stated reason is not the "two
    /// implementations that quietly disagree" problem — it is one file, one normaliser, and a
    /// documented difference in what is being asked.
    /// </summary>
    private static readonly Regex NotLearnedMarker = new(
        @"\b(?:"
        + @"have(?:n't| not) (?:learned|come across|heard of)|never (?:heard|learned|come across)"
        + @"|have(?:n't| not) been told|you never told|(?:no-one|nobody|nothing) (?:told|about)"
        + @"|new (?:to me|one on me)|not familiar|don't recognise|don't recognize"
        + @"|don't have (?:anything|much|a thing)|nothing about"
        + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    /// <summary>The patterns themselves, so a test can assert they carry no control characters.</summary>
    public static string Pattern => UncertaintyMarker + "|" + NotLearnedMarker;

    /// <summary>
    /// Does this reply mark something as not known? The frozen corpus gate — unchanged, and
    /// deliberately not widened, because the accepted-row record depends on it.
    /// </summary>
    public static bool Admits(string? reply)
        => UncertaintyMarker.IsMatch(Normalise((reply ?? "").Trim()));

    /// <summary>
    /// The serving-time question: did the reply satisfy an ADMIT obligation of either family?
    /// Used by the renderer's canary gate, where a false negative costs a needless fallback.
    /// </summary>
    public static bool AdmitsNotLearned(string? reply)
    {
        var text = Normalise((reply ?? "").Trim());
        return UncertaintyMarker.IsMatch(text) || NotLearnedMarker.IsMatch(text);
    }

    /// <summary>Fold typographic punctuation to ASCII, so patterns match what was written.</summary>
    public static string Normalise(string text)
        => text.Replace('’', '\'').Replace('‘', '\'')
            .Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-');
}
