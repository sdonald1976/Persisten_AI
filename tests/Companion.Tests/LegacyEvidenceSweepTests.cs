using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The residual from A1: derived rows written before lineage existed.
///
/// They carry no evidence identity, so they can neither be matched nor shown independent of
/// the turn being forgotten. Rather than purging them pre-emptively at migration time, they
/// are swept at the moment the user ACTUALLY invokes forgetting — which is the moment the
/// ambiguity stops being acceptable. This deliberately favours privacy over preserving
/// ambiguous derived state, and it is reported rather than silent.
///
/// Nothing here matches text and nothing invents lineage. The only question asked of a row
/// is whether it carries any identity at all, which it answers about itself.
/// </summary>
public class LegacyEvidenceSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";
    private const string Other = "usr-someone-else";
    private const string SameWords = "the shed roof needs replacing";

    private static Reflection Reflection(string userId, IEnumerable<Guid>? lineage) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, CreatedAt = Now,
        Musing = "He keeps putting the shed off.",
        SourceMessageIdsJson = lineage is null ? "[]" : EvidenceForgetting.WriteIds(lineage),
    };

    private static CompanionPreference Preference(string userId, IEnumerable<Guid>? lineage) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Subject = "sheds", Affinity = 0.8,
        Confidence = 0.6, Reason = "he lights up about it", Observations = 3,
        CreatedAt = Now, UpdatedAt = Now,
        EvidenceMessageIdsJson = lineage is null ? "[]" : EvidenceForgetting.WriteIds(lineage),
    };

    private static KnowledgeGap Gap(string userId, IEnumerable<Guid>? lineage) => new()
    {
        Id = Guid.NewGuid(), UserId = userId, Kind = GapKind.UnknownConcept, Subject = "quokka",
        Source = GapSource.KnowledgeLookup, Occurrences = 1, FirstSeen = Now, LastSeen = Now,
        EvidenceMessageIdsJson = lineage is null ? "[]" : EvidenceForgetting.WriteIds(lineage),
    };

    // ---- modern lineage is matched exactly, legacy is swept ---------------------------------

    [Fact]
    public async Task ExactLineageIsMatched_AndUnrelatedModernRowsSurvive()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var doomed = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var hit = Reflection(User, [doomed]);
        var miss = Reflection(User, [unrelated]);
        db.Reflections.AddRange(hit, miss);
        await db.SaveChangesAsync();

        await sp.GetRequiredService<IReflectionStore>()
            .ForgetByEvidenceAsync(User, [doomed], Now);

        Assert.Null((await db.Reflections.AsNoTracking().SingleAsync(r => r.Id == hit.Id)).Musing);
        // A modern row with different lineage PROVES independence and survives.
        Assert.NotNull((await db.Reflections.AsNoTracking().SingleAsync(r => r.Id == miss.Id)).Musing);
    }

    [Fact]
    public async Task LegacyRowsForTheSameUser_AreSwept()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var legacyReflection = Reflection(User, null);
        var legacyPreference = Preference(User, null);
        var legacyGap = Gap(User, null);
        db.Reflections.Add(legacyReflection);
        db.CompanionPreferences.Add(legacyPreference);
        db.KnowledgeGaps.Add(legacyGap);
        await db.SaveChangesAsync();

        // The forgotten message has nothing to do with these rows. They go anyway, because
        // they cannot show they are independent of it.
        var unrelated = Guid.NewGuid();
        await sp.GetRequiredService<IReflectionStore>().ForgetByEvidenceAsync(User, [unrelated], Now);
        await sp.GetRequiredService<IPreferenceStore>().ForgetByEvidenceAsync(User, [unrelated], Now);
        await sp.GetRequiredService<IGapStore>().ForgetByEvidenceAsync(User, [unrelated], Now);

        Assert.Null((await db.Reflections.AsNoTracking().SingleAsync()).Musing);
        Assert.True((await db.CompanionPreferences.AsNoTracking().SingleAsync()).EvidenceForgotten);
        Assert.Equal(GapStatus.EvidenceForgotten,
            (await db.KnowledgeGaps.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task LegacyRowsForAnotherUser_AreNeverTouched()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // Identical shape, identical wording, different owner.
        db.Reflections.AddRange(Reflection(User, null), Reflection(Other, null));
        db.CompanionPreferences.AddRange(Preference(User, null), Preference(Other, null));
        db.KnowledgeGaps.AddRange(Gap(User, null), Gap(Other, null));
        await db.SaveChangesAsync();

        var unrelated = Guid.NewGuid();
        await sp.GetRequiredService<IReflectionStore>().ForgetByEvidenceAsync(User, [unrelated], Now);
        await sp.GetRequiredService<IPreferenceStore>().ForgetByEvidenceAsync(User, [unrelated], Now);
        await sp.GetRequiredService<IGapStore>().ForgetByEvidenceAsync(User, [unrelated], Now);

        Assert.NotNull((await db.Reflections.AsNoTracking().SingleAsync(r => r.UserId == Other)).Musing);
        Assert.False((await db.CompanionPreferences.AsNoTracking()
            .SingleAsync(p => p.UserId == Other)).EvidenceForgotten);
        Assert.Equal(GapStatus.Open,
            (await db.KnowledgeGaps.AsNoTracking().SingleAsync(g => g.UserId == Other)).Status);
    }

    [Fact]
    public async Task AWorldSourcedPerspectiveProvesIndependence_AndSurvives()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var world = new Experience
        {
            Id = Guid.NewGuid(), UserId = User, At = Now, Source = "world", Kind = "saw",
            Text = SameWords, EvidenceMessageId = null,
        };
        var chat = new Experience
        {
            Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat", Kind = "said",
            Text = SameWords, EvidenceMessageId = null,        // legacy: no lineage
        };
        db.Experiences.AddRange(world, chat);
        db.SharedExperiencePerspectives.AddRange(
            new SharedExperiencePerspective
            {
                Id = Guid.NewGuid(), UserId = User, ExperienceId = world.Id,
                Owner = MemoryOwner.Shared, Summary = SameWords, Confidence = 0.5,
                Evidence = SameWords, CreatedAt = Now,
            },
            new SharedExperiencePerspective
            {
                Id = Guid.NewGuid(), UserId = User, ExperienceId = chat.Id,
                Owner = MemoryOwner.Shared, Summary = SameWords, Confidence = 0.5,
                Evidence = SameWords, CreatedAt = Now,
            });
        await db.SaveChangesAsync();

        await sp.GetRequiredService<ISharedPerspectiveStore>()
            .ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now);

        // The world-sourced one survives: it came from a perception, never from a message.
        var left = await db.SharedExperiencePerspectives.AsNoTracking().ToListAsync();
        Assert.Single(left);
        Assert.Equal(world.Id, left[0].ExperienceId);
    }

    // ---- the properties that make it safe -----------------------------------------------------

    [Fact]
    public async Task TheSweepIsIdempotent()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        db.Reflections.Add(Reflection(User, null));
        await db.SaveChangesAsync();

        var store = sp.GetRequiredService<IReflectionStore>();
        var unrelated = Guid.NewGuid();
        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [unrelated], Now));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [unrelated], Now));
        Assert.Equal(0, await store.ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now));
    }

    [Fact]
    public async Task TheSweepSurvivesRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.db");
        try
        {
            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                using var scope = host.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
                db.Reflections.Add(Reflection(User, null));
                await db.SaveChangesAsync();

                await scope.ServiceProvider.GetRequiredService<IReflectionStore>()
                    .ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now);
            }

            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                using var scope = host.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
                var row = await db.Reflections.AsNoTracking().SingleAsync();

                Assert.Null(row.Musing);
                Assert.True(row.EvidenceForgotten);
                Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IReflectionStore>()
                    .ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now));
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AfterForgetting_NothingSweptReachesThePrompt()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        db.CompanionPreferences.Add(Preference(User, null));
        await db.SaveChangesAsync();

        var preferences = sp.GetRequiredService<IPreferenceStore>();
        Assert.NotEmpty(await preferences.GetAllAsync(User));

        await preferences.ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now);

        // Excluded at the source, so no prompt, retrieval, resolution or comparison can see it.
        Assert.Empty(await preferences.GetAllAsync(User));
    }

    [Fact]
    public void TheSweepRuleReadsNoText()
    {
        // Structural: the decision is about the PRESENCE of an identity, never about content.
        var method = typeof(EvidenceForgetting)
            .GetMethod(nameof(EvidenceForgetting.HasNoLineage))!;

        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.Equal("lineageJson", parameter.Name);

        // Identical wording, different lineage state, opposite answers — the text is irrelevant.
        Assert.True(EvidenceForgetting.HasNoLineage("[]"));
        Assert.True(EvidenceForgetting.HasNoLineage(null));
        Assert.False(EvidenceForgetting.HasNoLineage(
            EvidenceForgetting.WriteIds([Guid.NewGuid()])));
    }

    [Fact]
    public async Task IdenticalWordingIsNeverTheReason()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        // Same musing text; one has lineage that does not match, one has none.
        var withLineage = Reflection(User, [Guid.NewGuid()]);
        var withoutLineage = Reflection(User, null);
        db.Reflections.AddRange(withLineage, withoutLineage);
        await db.SaveChangesAsync();

        await sp.GetRequiredService<IReflectionStore>()
            .ForgetByEvidenceAsync(User, [Guid.NewGuid()], Now);

        // The one that could prove independence survives; the one that could not, did not.
        // The wording was identical in both, so wording cannot have been the reason.
        Assert.NotNull((await db.Reflections.AsNoTracking()
            .SingleAsync(r => r.Id == withLineage.Id)).Musing);
        Assert.Null((await db.Reflections.AsNoTracking()
            .SingleAsync(r => r.Id == withoutLineage.Id)).Musing);
    }

    // ---- the redacted-preference invariant -----------------------------------------------------

    [Fact]
    public async Task ARedactedPreferenceContributesNothing_ButKeepsContentFreeHistory()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var preferences = sp.GetRequiredService<IPreferenceStore>();

        var message = Guid.NewGuid();
        db.CompanionPreferences.Add(Preference(User, [message]));
        await db.SaveChangesAsync();

        await preferences.ForgetByEvidenceAsync(User, [message], Now);

        Assert.Empty(await preferences.GetAllAsync(User));

        var row = await db.CompanionPreferences.AsNoTracking().SingleAsync();
        // Semantic content absent...
        Assert.Equal(string.Empty, row.Subject);
        Assert.Null(row.Reason);
        Assert.Null(row.Embedding);
        // ...content-free numeric history retained, which is only acceptable BECAUSE the row
        // is unreadable above. Affinity is not reset to neutral: inventing a reading would
        // assert something about the user that was never true.
        Assert.Equal(0.8, row.Affinity);
        Assert.Equal(3, row.Observations);
    }

    [Fact]
    public async Task ARedactedPreferenceIsNeverRevivedByALaterSignal()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
        var preferences = sp.GetRequiredService<IPreferenceStore>();

        var message = Guid.NewGuid();
        db.CompanionPreferences.Add(Preference(User, [message]));
        await db.SaveChangesAsync();
        await preferences.ForgetByEvidenceAsync(User, [message], Now);

        // A later signal about the same subject must create a NEW preference, never wake the
        // forgotten one and carry its old affinity back into the prompt.
        var fresh = await preferences.ApplySignalAsync(
            User, "sheds", 0.2, "mentioned once", null, Now.AddDays(1), [Guid.NewGuid()]);

        Assert.False(fresh.EvidenceForgotten);
        Assert.Equal(1, fresh.Observations);
        Assert.Equal(2, await db.CompanionPreferences.AsNoTracking().CountAsync());
        Assert.Single(await preferences.GetAllAsync(User));
    }
}
