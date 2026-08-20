using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase 4 (docs/KNOWLEDGE_GAPS.md): typed knowledge gaps feeding the EXISTING curiosity
/// lifecycle. The governing invariant: a gap important enough to record is not thereby
/// important enough to interrupt conversation — promotion is a separate, capped,
/// suppression-reasoned decision, and one gap earns at most one curiosity, ever.
/// </summary>
public class KnowledgeGapTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "gap-user";

    private static TestHost Host() => new(Now,
        settings: new Dictionary<string, string?> { ["CognitiveModels:Capture"] = "true" });

    private static async Task<Guid> ConversationAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(UserId, "t", "mock", "test")).Id;
    }

    private static async Task SayAsync(TestHost host, Guid conv, string message)
    {
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(UserId, conv, message);
    }

    private static async Task<int> PromoteAsync(TestHost host)
    {
        using var scope = host.CreateScope();
        var reflections = scope.ServiceProvider.GetRequiredService<IReflectionStore>();
        var reflection = new Reflection { Id = Guid.NewGuid(), UserId = UserId, CreatedAt = Now, CoveredThrough = Now };
        await reflections.AddAsync(reflection, Array.Empty<Curiosity>());
        return await scope.ServiceProvider.GetRequiredService<GapPromoter>()
            .PromoteAsync(UserId, reflection.Id);
    }

    // ---- observation: dedupe, occurrence, provenance ----

    [Fact]
    public async Task ObservingTwice_BumpsOccurrences_NeverDuplicates()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var gaps = scope.ServiceProvider.GetRequiredService<IGapStore>();

        var (first, created1) = await gaps.ObserveAsync(
            UserId, GapKind.UnknownConcept, "quokka", GapSource.KnowledgeLookup, Guid.NewGuid(), Now);
        var (second, created2) = await gaps.ObserveAsync(
            UserId, GapKind.UnknownConcept, "quokka", GapSource.KnowledgeLookup, Guid.NewGuid(), Now.AddMinutes(5));

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Occurrences);
        Assert.Single(await gaps.GetRecentAsync(UserId, 10));
    }

    [Fact]
    public async Task AnUnknownKnowledgeQuestion_ObservesAGap_WithTurnProvenance()
    {
        await using var host = Host();
        var conv = await ConversationAsync(host);

        await SayAsync(host, conv, "Do you know what a quokka is?");

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("unknown-concept:quokka",
            turn.Decisions.Single(d => d.Stage == "gap.observed").Verdict);

        using var scope = host.CreateScope();
        var gap = Assert.Single(await scope.ServiceProvider.GetRequiredService<IGapStore>()
            .GetOpenAsync(UserId));
        Assert.Equal(GapKind.UnknownConcept, gap.Kind);
        Assert.Equal(turn.TraceId, gap.SourceRef); // provenance to the observing turn
    }

    // ---- promotion: separate decision, one per pass, suppression reasons recorded ----

    [Fact]
    public async Task Promotion_MintsOneCuriosity_AndRecordsEverySuppression()
    {
        await using var host = Host();
        using (var scope = host.CreateScope())
        {
            var gaps = scope.ServiceProvider.GetRequiredService<IGapStore>();
            await gaps.ObserveAsync(UserId, GapKind.UnknownConcept, "quokka", GapSource.KnowledgeLookup, null, Now);
            await gaps.ObserveAsync(UserId, GapKind.UnknownConcept, "petrichor", GapSource.KnowledgeLookup, null, Now.AddMinutes(1));
            await gaps.ObserveAsync(UserId, GapKind.UnresolvedReference, "her", GapSource.WorkingContext, null, Now.AddMinutes(2));
            await gaps.ObserveAsync(UserId, GapKind.UnknownConcept, "zarf", GapSource.WorkingContext, null, Now.AddMinutes(3));
        }

        var promoted = await PromoteAsync(host);

        Assert.Equal(1, promoted);
        using var verify = host.CreateScope();
        var reflections = verify.ServiceProvider.GetRequiredService<IReflectionStore>();
        var curiosity = Assert.Single(await reflections.GetOpenCuriositiesAsync(UserId));
        Assert.Equal("quokka", curiosity.About);          // oldest explicit lookup wins the tie
        Assert.NotNull(curiosity.GapId);
        Assert.Contains("quokka", curiosity.Question);

        var quokkaGap = (await verify.ServiceProvider.GetRequiredService<IGapStore>()
            .GetRecentAsync(UserId, 10)).Single(g => g.Subject == "quokka");
        Assert.Equal(GapStatus.Pursuing, quokkaGap.Status);
        Assert.Equal(curiosity.Id, quokkaGap.CuriosityId);

        // Every considered gap left a promoted-or-suppressed row with its reason.
        var captures = await verify.ServiceProvider.GetRequiredService<IShadowRecorder>()
            .GetCapturesAsync("gap.promotion", 20);
        var byInput = captures.ToDictionary(c => c.Input!, c => c.Legacy);
        Assert.Equal("promoted", byInput.Single(kv => kv.Key.Contains("quokka")).Value);
        Assert.Equal("cap-reached", byInput.Single(kv => kv.Key.Contains("petrichor")).Value);
        Assert.Equal("kind-not-promotable", byInput.Single(kv => kv.Key.Contains("her")).Value);
        Assert.Equal("below-floor", byInput.Single(kv => kv.Key.Contains("zarf")).Value);
    }

    [Fact]
    public async Task ARepeatedUnknown_GainsSalience_ButNeverASecondCuriosity()
    {
        await using var host = Host();
        var conv = await ConversationAsync(host);

        await SayAsync(host, conv, "Do you know what a quokka is?");
        Assert.Equal(1, await PromoteAsync(host));

        // Asked about again after promotion: occurrences bump on the SAME (now Pursuing)
        // gap; a second pass mints nothing new.
        await SayAsync(host, conv, "Do you know what a quokka is?");
        Assert.Equal(0, await PromoteAsync(host));

        using var scope = host.CreateScope();
        var gap = (await scope.ServiceProvider.GetRequiredService<IGapStore>()
            .GetRecentAsync(UserId, 10)).Single(g => g.Subject == "quokka");
        Assert.Equal(2, gap.Occurrences);
        Assert.Equal(GapStatus.Pursuing, gap.Status);

        // Exactly ONE quokka curiosity ever exists — and the existing machinery already
        // voiced it during the repeat turn (asked once is the whole budget; this test
        // originally expected it still Open and caught the voicing working correctly).
        var db = scope.ServiceProvider.GetRequiredService<Companion.Infrastructure.Persistence.CompanionDbContext>();
        var quokkaCuriosity = Assert.Single(db.Curiosities.Where(c => c.About == "quokka").ToList());
        Assert.Equal(CuriosityStatus.Voiced, quokkaCuriosity.Status);
    }

    // ---- the restraint control: five unknowns, at most one question ----

    [Fact]
    public async Task FiveUnknowns_YieldFiveGaps_AndAtMostOneCuriosity()
    {
        await using var host = Host();
        var conv = await ConversationAsync(host);

        foreach (var term in new[] { "quokka", "zarf", "petrichor", "murmuration", "bolide" })
            await SayAsync(host, conv, $"Do you know what a {term} is?");

        using (var scope = host.CreateScope())
        {
            Assert.Equal(5, (await scope.ServiceProvider.GetRequiredService<IGapStore>()
                .GetOpenAsync(UserId)).Count);
        }

        var promoted = await PromoteAsync(host);

        Assert.Equal(1, promoted);
        using var verify = host.CreateScope();
        Assert.Single((await verify.ServiceProvider.GetRequiredService<IReflectionStore>()
            .GetOpenCuriositiesAsync(UserId)).Where(c => c.GapId is not null));
    }

    // ---- satisfaction: teaching closes the loop, curiosity included ----

    [Fact]
    public async Task Teaching_SatisfiesTheGap_AndItsCuriosity()
    {
        await using var host = Host();
        var conv = await ConversationAsync(host);

        await SayAsync(host, conv, "Do you know what a quokka is?");
        await PromoteAsync(host);
        await SayAsync(host, conv, "A quokka is a small wallaby native to Western Australia.");

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("quokka", turn.Decisions.Single(d => d.Stage == "gap.satisfied").Verdict);

        using var scope = host.CreateScope();
        var gap = (await scope.ServiceProvider.GetRequiredService<IGapStore>()
            .GetRecentAsync(UserId, 10)).Single(g => g.Subject == "quokka");
        Assert.Equal(GapStatus.Satisfied, gap.Status);
        Assert.Contains("learned from teaching", gap.ResolutionNote);

        var db = scope.ServiceProvider.GetRequiredService<Companion.Infrastructure.Persistence.CompanionDbContext>();
        var curiosity = db.Curiosities.Single(c => c.Id == gap.CuriosityId);
        Assert.Equal(CuriosityStatus.Satisfied, curiosity.Status);

        // And she now genuinely knows it (Phase 3).
        Assert.Equal(ConceptFamiliarity.Known,
            (await scope.ServiceProvider.GetRequiredService<IConceptKnowledge>()
                .LookupAsync(UserId, "quokka")).Familiarity);
    }

    [Fact]
    public async Task StaleGaps_AgeToExpired_NotToQuestions()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var gaps = scope.ServiceProvider.GetRequiredService<IGapStore>();
        await gaps.ObserveAsync(UserId, GapKind.UnknownConcept, "zarf", GapSource.WorkingContext, null,
            Now - TimeSpan.FromDays(40));

        var expired = await gaps.ExpireStaleAsync(UserId, Now - SleepCycle.StaleGapAge);

        Assert.Equal(1, expired);
        var gap = Assert.Single(await gaps.GetRecentAsync(UserId, 10));
        Assert.Equal(GapStatus.Expired, gap.Status);
        Assert.Equal("aged out unpursued", gap.ResolutionNote);
    }
}
