namespace Companion.Core.Domain;

/// <summary>
/// One of the companion's OWN tastes — "Ava has a moderately positive opinion of Alien" — kept
/// strictly separate from what the user likes. The user loving something never writes here;
/// preferences only form and move through her own reflection on real conversations, one small
/// step at a time, so an established taste can't be overwritten by a single casual remark.
/// This separation is what makes honest agreement AND honest disagreement possible.
/// </summary>
public sealed class CompanionPreference
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = default!;

    /// <summary>What the preference is about ("horror movies", "Alien", "small talk").</summary>
    public string Subject { get; set; } = default!;

    /// <summary>[-1, 1]: how she feels about it. 0 is genuinely mixed/undecided.</summary>
    public double Affinity { get; set; }

    /// <summary>[0, 1]: how established the taste is — grows with corroborating experiences.</summary>
    public double Confidence { get; set; }

    /// <summary>Why she currently feels this way (latest reasoning, from her own reflection).</summary>
    public string? Reason { get; set; }

    /// <summary>Every emotional-signal message id that contributed an observation.</summary>
    public string EvidenceMessageIdsJson { get; set; } = "[]";

    /// <summary>Evidence forgotten: subject and reason are gone and it is never returned
    /// to the prompt.</summary>
    public bool EvidenceForgotten { get; set; }

    /// <summary>How many separate experiences have shaped this preference.</summary>
    public int Observations { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Embedding of subject+reason, so relevant tastes surface in the right conversations.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>
    /// The preference in natural words for the prompt — never raw numbers. Includes how settled
    /// it is, so the model can hold new tastes loosely and established ones firmly.
    /// </summary>
    public string Describe()
    {
        var feeling = Affinity switch
        {
            >= 0.6 => "really likes",
            >= 0.25 => "has a moderately positive opinion of",
            > -0.25 => "has mixed, unsettled feelings about",
            > -0.6 => "isn't keen on",
            _ => "really dislikes",
        };
        var line = $"You {StripYou(feeling)} {Subject}";
        if (!string.IsNullOrWhiteSpace(Reason))
            line += $" — {Reason!.TrimEnd('.')}";
        if (Confidence < 0.4)
            line += " (a newer taste — you're still making up your mind)";
        return line + ".";
    }

    public string Describe(string companionName)
    {
        var who = string.IsNullOrWhiteSpace(companionName) ? "The companion" : companionName.Trim();
        var feeling = Affinity switch
        {
            >= 0.6 => "really likes",
            >= 0.25 => "has a moderately positive opinion of",
            > -0.25 => "has mixed, unsettled feelings about",
            > -0.6 => "isn't keen on",
            _ => "really dislikes",
        };
        var line = $"{who} {feeling} {Subject}";
        if (!string.IsNullOrWhiteSpace(Reason))
            line += $" - {Reason!.TrimEnd('.')}";
        if (Confidence < 0.4)
            line += $" (a newer taste - {who} is still settling it)";
        return line + ".";
    }

    private static string StripYou(string thirdPerson) => thirdPerson switch
    {
        "really likes" => "really like",
        "has a moderately positive opinion of" => "have a moderately positive opinion of",
        "has mixed, unsettled feelings about" => "have mixed, unsettled feelings about",
        "isn't keen on" => "aren't keen on",
        _ => "really dislike",
    };
}
