using System.Text.Json;
using Companion.Core.Domain;

namespace Companion.Core.Services;

/// <summary>
/// The forgetting rules for derived records, as pure functions over already-loaded rows.
///
/// Every rule matches on EXACT durable identity — a message id — and never on text. That is
/// not a stylistic preference: these records hold Ava's prose about the user, so matching
/// their wording would delete by resemblance to something the user never said, and miss a
/// paraphrase of something they did.
///
/// A derived record may have MANY evidence parents. Where it does, the policy is stated per
/// record type below and is never inferred at the call site:
///
///   <list type="bullet">
///   <item>Experience — one parent. <b>Redact</b>: text removed, row kept so age-based
///   pruning still has something to sweep.</item>
///   <item>Reflection — many parents. <b>Redact on any parent</b>: a musing drawn from five
///   turns cannot be recomputed without the forgotten one, and partially-true prose is worse
///   than none. Thread structure survives because it is content-free.</item>
///   <item>Curiosity — inherits its reflection's parents. <b>Redact</b> to a terminal status
///   that is never voiced and never selected.</item>
///   <item>AttentionItem — one parent. <b>Delete</b>: a salience marker has no audit value
///   and nothing references it.</item>
///   <item>CompanionPreference — many parents. <b>Redact on any parent</b>: affinity was
///   accumulated from observations that cannot be re-derived. Deliberately NOT recomputed to
///   a neutral value, because inventing a reading is worse than having none.</item>
///   <item>SharedExperiencePerspective — one parent (its experience). <b>Delete</b>:
///   commentary on a forgotten experience has nothing left to comment on.</item>
///   <item>KnowledgeGap — many parents, accumulated. <b>Sever and recompute</b>: forgotten
///   ids are removed and the occurrence count falls to what remains; only an empty list
///   retires the gap. This is the one record where remaining evidence still supports it.</item>
///   <item>TurnRecord — one parent. <b>Redact</b>: every derived text column is nulled,
///   leaving the content-free metrics the diagnostics endpoint reports.</item>
///   </list>
///
/// User identity is not checked here. It is applied by the STORE, in the query that loads
/// the rows, so a caller cannot reach another user's data by passing the wrong list — the
/// isolation is structural rather than a check these functions could forget to make.
/// </summary>
public static class EvidenceForgetting
{
    /// <summary>Reads a serialized id list, tolerating null, empty and malformed values.</summary>
    public static List<Guid> ReadIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A row whose lineage cannot be parsed is treated as having none, which makes it
            // unforgettable-by-identity rather than wrongly attributed to somebody's message.
            return [];
        }
    }

    public static string WriteIds(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Distinct().ToList());

    // ---- one parent ---------------------------------------------------------------------

    /// <summary>Redacts experiences whose message was forgotten. Returns how many changed.</summary>
    public static int ForgetExperiences(
        IEnumerable<Experience> experiences, IReadOnlyCollection<Guid> forgotten)
    {
        var n = 0;
        foreach (var e in experiences)
        {
            if (e.EvidenceForgotten) continue;                  // idempotent
            if (e.EvidenceMessageId is not { } id || !forgotten.Contains(id)) continue;
            e.Text = string.Empty;
            e.EvidenceForgotten = true;
            n++;
        }
        return n;
    }

    /// <summary>Redacts turn diagnostics whose message was forgotten.</summary>
    public static int ForgetTurnRecords(
        IEnumerable<TurnRecord> records, IReadOnlyCollection<Guid> forgotten)
    {
        var n = 0;
        foreach (var r in records)
        {
            if (r.SourceMessageId is not { } id || !forgotten.Contains(id)) continue;
            if (r.UserPreview is null && r.AssistantPreview is null && r.RetrievalQuery is null
                && r.Retrieved is null && r.Plan is null && r.FocalTerms is null
                && r.BoundQuestion is null && r.ResolvedReference is null)
                continue;                                        // already redacted or privacy-skipped

            r.UserPreview = null;
            r.AssistantPreview = null;
            r.RetrievalQuery = null;
            r.Retrieved = null;
            r.Plan = null;
            r.FocalTerms = null;
            r.BoundQuestion = null;
            r.ResolvedReference = null;
            n++;
        }
        return n;
    }

    // ---- many parents -------------------------------------------------------------------

    /// <summary>Redacts reflections drawing on any forgotten message.</summary>
    public static int ForgetReflections(
        IEnumerable<Reflection> reflections, IReadOnlyCollection<Guid> forgotten,
        out HashSet<Guid> redactedIds)
    {
        redactedIds = [];
        var n = 0;
        foreach (var r in reflections)
        {
            if (r.EvidenceForgotten) continue;
            var sources = ReadIds(r.SourceMessageIdsJson);
            if (!sources.Any(forgotten.Contains)) continue;

            r.Musing = null;
            r.Embedding = null;                                  // derived from the musing
            r.SourceMessageIdsJson = WriteIds(sources.Where(s => !forgotten.Contains(s)));
            r.EvidenceForgotten = true;
            redactedIds.Add(r.Id);
            n++;
        }
        return n;
    }

    /// <summary>Retires curiosities whose reflection was redacted.</summary>
    public static int ForgetCuriosities(
        IEnumerable<Curiosity> curiosities, IReadOnlyCollection<Guid> redactedReflectionIds)
    {
        var n = 0;
        foreach (var c in curiosities)
        {
            if (c.Status == CuriosityStatus.EvidenceForgotten) continue;
            if (!redactedReflectionIds.Contains(c.ReflectionId)) continue;
            c.Question = string.Empty;
            c.About = null;
            c.Reason = null;
            c.Status = CuriosityStatus.EvidenceForgotten;
            n++;
        }
        return n;
    }

    /// <summary>Redacts companion preferences observed from any forgotten message.</summary>
    public static int ForgetCompanionPreferences(
        IEnumerable<CompanionPreference> preferences, IReadOnlyCollection<Guid> forgotten)
    {
        var n = 0;
        foreach (var p in preferences)
        {
            if (p.EvidenceForgotten) continue;
            var sources = ReadIds(p.EvidenceMessageIdsJson);
            if (!sources.Any(forgotten.Contains)) continue;

            p.Subject = string.Empty;
            p.Reason = null;
            p.Embedding = null;
            p.EvidenceMessageIdsJson = WriteIds(sources.Where(s => !forgotten.Contains(s)));
            p.EvidenceForgotten = true;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Severs forgotten evidence from gaps and recomputes what remains. A gap with surviving
    /// evidence keeps its subject and simply counts fewer occurrences; only a gap with none
    /// left is retired and redacted.
    /// </summary>
    public static int ForgetKnowledgeGaps(
        IEnumerable<KnowledgeGap> gaps, IReadOnlyCollection<Guid> forgotten)
    {
        var n = 0;
        foreach (var g in gaps)
        {
            if (g.Status == GapStatus.EvidenceForgotten) continue;
            var sources = ReadIds(g.EvidenceMessageIdsJson);
            if (!sources.Any(forgotten.Contains)) continue;

            var remaining = sources.Where(s => !forgotten.Contains(s)).ToList();
            g.EvidenceMessageIdsJson = WriteIds(remaining);

            if (remaining.Count > 0)
            {
                // Deterministic recompute: the gap stands on what is left.
                g.Occurrences = remaining.Count;
            }
            else
            {
                g.Subject = string.Empty;
                g.ResolutionNote = null;
                g.Occurrences = 0;
                g.Status = GapStatus.EvidenceForgotten;
            }
            n++;
        }
        return n;
    }

    // ---- legacy rows, swept at the moment forgetting is actually invoked -----------------

    /// <summary>
    /// Whether a derived row can PROVE it is independent of the forgotten evidence.
    ///
    /// A row written before lineage existed carries no id at all. It cannot be matched, and
    /// it cannot be cleared either — so at the moment a user actually invokes forgetting, the
    /// honest reading is that it might well have come from the turn they are removing. The
    /// policy is to favour privacy over preserving ambiguous derived state, once, at exactly
    /// that moment.
    ///
    /// This is NOT text matching and NOT invented lineage. Nothing is attributed to a
    /// message. The question asked is only "does this row carry any lineage at all", which a
    /// row answers about itself.
    /// </summary>
    public static bool HasNoLineage(string? lineageJson) => ReadIds(lineageJson).Count == 0;

    /// <summary>Redacts a user's lineage-less reflections, and retires what they anchored.</summary>
    public static int SweepLegacyReflections(
        IEnumerable<Reflection> reflections, out HashSet<Guid> redactedIds)
    {
        redactedIds = [];
        var n = 0;
        foreach (var r in reflections)
        {
            if (r.EvidenceForgotten || !HasNoLineage(r.SourceMessageIdsJson)) continue;
            r.Musing = null;
            r.Embedding = null;
            r.EvidenceForgotten = true;
            redactedIds.Add(r.Id);
            n++;
        }
        return n;
    }

    /// <summary>Redacts a user's lineage-less companion preferences.</summary>
    public static int SweepLegacyCompanionPreferences(IEnumerable<CompanionPreference> preferences)
    {
        var n = 0;
        foreach (var p in preferences)
        {
            if (p.EvidenceForgotten || !HasNoLineage(p.EvidenceMessageIdsJson)) continue;
            p.Subject = string.Empty;
            p.Reason = null;
            p.Embedding = null;
            p.EvidenceForgotten = true;
            n++;
        }
        return n;
    }

    /// <summary>Retires a user's lineage-less knowledge gaps.</summary>
    public static int SweepLegacyKnowledgeGaps(IEnumerable<KnowledgeGap> gaps)
    {
        var n = 0;
        foreach (var g in gaps)
        {
            if (g.Status == GapStatus.EvidenceForgotten
                || !HasNoLineage(g.EvidenceMessageIdsJson)) continue;
            g.Subject = string.Empty;
            g.ResolutionNote = null;
            g.Occurrences = 0;
            g.Status = GapStatus.EvidenceForgotten;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Which of a user's shared perspectives cannot prove independence.
    ///
    /// A perspective comments on ONE experience, so its parent answers the question. A
    /// world-sourced experience is a structural PROOF of independence — it came from the
    /// world link and never from a message — so its perspective survives. A parent that is
    /// missing, or that carries no lineage and is not world-sourced, proves nothing.
    /// </summary>
    public static bool PerspectiveProvesIndependence(Experience? parent)
        => parent is { EvidenceMessageId: null }
           && string.Equals(parent.Source, "world", StringComparison.OrdinalIgnoreCase);
}
