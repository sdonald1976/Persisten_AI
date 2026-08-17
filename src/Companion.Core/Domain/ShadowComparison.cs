namespace Companion.Core.Domain;

/// <summary>
/// One occasion where a specialist model was asked the same question as the heuristic it might one
/// day replace, and what each of them said.
///
/// This is the evidence that lets a model be adopted for a reason. Without it the choice is made on
/// how the replies read, which is exactly how the memory store spent weeks holding facts nobody had
/// stated while sounding entirely coherent — the reply is generated from a transcript that is still
/// in the prompt, so it looks right for the whole session in which the damage is done.
///
/// The model's answer is <em>recorded</em>, never used, while it is in shadow. The production
/// decision stays the heuristic's, and <see cref="Applied"/> says so explicitly rather than leaving
/// it to be inferred from a configuration flag whose value at the time is not in the record.
/// </summary>
public class ShadowComparison
{
    public Guid Id { get; set; }

    /// <summary>The judgment being compared: "reranker.relevance", "nli.supersession", "classifier.decision".</summary>
    public string Subject { get; set; } = default!;

    /// <summary>The heuristic's answer, as a short canonical string ("true", "contradiction", "3").</summary>
    public string? Legacy { get; set; }

    /// <summary>The model's answer, in the same vocabulary, so the two are directly comparable.</summary>
    public string? Model { get; set; }

    /// <summary>How sure the model was (0..1), which is most of the value: disagreements at 0.51 and at 0.99 are different problems.</summary>
    public double Confidence { get; set; }

    /// <summary>Whether they agreed. Stored rather than computed so a query can count without parsing.</summary>
    public bool Agreed { get; set; }

    /// <summary>Which answer the turn actually acted on — "legacy" while shadowing, "model" once promoted.</summary>
    public string Applied { get; set; } = default!;

    /// <summary>Inference cost, for "how much latency does this add" — the other half of whether it is worth it.</summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// The text judged, when capture is switched on. Off by default and never written for a
    /// conversation the user put off the record: the rest of this table follows the same rule as
    /// <see cref="ModelCallRecord"/> and holds sizes and outcomes rather than content, so that it
    /// can be kept for weeks without quietly becoming a second, unguarded conversation store.
    ///
    /// It exists at all because a disagreement rate on its own answers the wrong question. Knowing
    /// they differ 12% of the time does not say which was right, and only the sentence does.
    /// </summary>
    public string? Input { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>How often a model and the heuristic it shadows agreed, over a window (computed, not stored).</summary>
public sealed record ShadowAgreement
{
    public required string Subject { get; init; }
    public required int Comparisons { get; init; }
    public required int Disagreements { get; init; }
    public required double AverageConfidence { get; init; }
    public required double AverageDurationMs { get; init; }

    /// <summary>Share of comparisons where the two agreed, 0..1.</summary>
    public double AgreementRate => Comparisons == 0 ? 0 : (Comparisons - Disagreements) / (double)Comparisons;
}
