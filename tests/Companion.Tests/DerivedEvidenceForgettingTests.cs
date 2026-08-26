using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// A1. `/forget` reaches the derived record types, by exact message identity only.
///
/// The audit found `/forget` reaching six of the fourteen tables holding user-derived
/// content. These tests pin the other eight — and pin the property that makes them safe:
/// identical wording produced by different events, or by different users, is never touched.
/// </summary>
public class DerivedEvidenceForgettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string User = "usr-scott";
    private const string Other = "usr-someone-else";

    // The same sentence, used by two different users and two different events, so every test
    // below is a real test of identity rather than of text.
    private const string SameWords = "the shed roof needs replacing";

    // ---- the pure rules ---------------------------------------------------------------------

    [Fact]
    public void Experiences_RedactOnlyTheMatchingEvent()
    {
        var doomed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var rows = new[]
        {
            new Experience { Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat",
                             Kind = "said", Text = SameWords, EvidenceMessageId = doomed },
            // Identical wording, different event.
            new Experience { Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat",
                             Kind = "said", Text = SameWords, EvidenceMessageId = kept },
            // World perception: no message parent at all.
            new Experience { Id = Guid.NewGuid(), UserId = User, At = Now, Source = "world",
                             Kind = "saw", Text = SameWords, EvidenceMessageId = null },
        };

        Assert.Equal(1, EvidenceForgetting.ForgetExperiences(rows, [doomed]));

        Assert.Equal(string.Empty, rows[0].Text);
        Assert.True(rows[0].EvidenceForgotten);
        Assert.Equal(SameWords, rows[1].Text);          // same words, different event
        Assert.Equal(SameWords, rows[2].Text);          // no parent, never attributed
        Assert.Equal(0, EvidenceForgetting.ForgetExperiences(rows, [doomed]));   // idempotent
    }

    [Fact]
    public void Reflections_RedactOnAnyParent_AndKeepContentFreeStructure()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var thread = Guid.NewGuid();
        var r = new Reflection
        {
            Id = Guid.NewGuid(), UserId = User, CreatedAt = Now,
            Musing = "He keeps putting the shed off.",
            Embedding = [0.1f, 0.2f],
            MessagesReflected = 2, ThreadId = thread,
            SourceMessageIdsJson = EvidenceForgetting.WriteIds([a, b]),
        };

        Assert.Equal(1, EvidenceForgetting.ForgetReflections([r], [a], out var redacted));

        Assert.Null(r.Musing);
        Assert.Null(r.Embedding);
        Assert.True(r.EvidenceForgotten);
        Assert.Contains(r.Id, redacted);
        // The surviving parent is still named; the forgotten one is gone from the lineage.
        Assert.Equal([b], EvidenceForgetting.ReadIds(r.SourceMessageIdsJson));
        // Content-free structure survives, because later reflections chain through it.
        Assert.Equal(thread, r.ThreadId);
        Assert.Equal(2, r.MessagesReflected);

        Assert.Equal(0, EvidenceForgetting.ForgetReflections([r], [a], out _));
    }

    [Fact]
    public void Curiosities_RetireWhenTheirReflectionIsRedacted()
    {
        var reflection = Guid.NewGuid();
        var c = new Curiosity
        {
            Id = Guid.NewGuid(), UserId = User, ReflectionId = reflection,
            Question = "Did the shed quote arrive?", About = "the shed", Reason = "he mentioned it",
            Status = CuriosityStatus.Open, CreatedAt = Now,
        };
        var unrelated = new Curiosity
        {
            Id = Guid.NewGuid(), UserId = User, ReflectionId = Guid.NewGuid(),
            Question = "Did the shed quote arrive?",      // identical wording
            Status = CuriosityStatus.Open, CreatedAt = Now,
        };

        Assert.Equal(1, EvidenceForgetting.ForgetCuriosities([c, unrelated], [reflection]));

        Assert.Equal(CuriosityStatus.EvidenceForgotten, c.Status);
        Assert.Equal(string.Empty, c.Question);
        Assert.Null(c.About);
        Assert.Null(c.Reason);
        Assert.Equal(CuriosityStatus.Open, unrelated.Status);   // same words, different parent
    }

    [Fact]
    public void KnowledgeGaps_SeverOneParentAndRecomputeFromTheRest()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var gap = new KnowledgeGap
        {
            Id = Guid.NewGuid(), UserId = User, Kind = GapKind.UnknownConcept, Subject = "quokka",
            Source = GapSource.KnowledgeLookup, Occurrences = 2, FirstSeen = Now, LastSeen = Now,
            Status = GapStatus.Open,
            EvidenceMessageIdsJson = EvidenceForgetting.WriteIds([a, b]),
        };

        // One parent forgotten: the gap SURVIVES on the remaining evidence.
        Assert.Equal(1, EvidenceForgetting.ForgetKnowledgeGaps([gap], [a]));
        Assert.Equal(GapStatus.Open, gap.Status);
        Assert.Equal("quokka", gap.Subject);
        Assert.Equal(1, gap.Occurrences);
        Assert.Equal([b], EvidenceForgetting.ReadIds(gap.EvidenceMessageIdsJson));

        // The last parent forgotten: now it retires and is redacted.
        Assert.Equal(1, EvidenceForgetting.ForgetKnowledgeGaps([gap], [b]));
        Assert.Equal(GapStatus.EvidenceForgotten, gap.Status);
        Assert.Equal(string.Empty, gap.Subject);
        Assert.Equal(0, gap.Occurrences);
        Assert.Equal(0, EvidenceForgetting.ForgetKnowledgeGaps([gap], [b]));
    }

    [Fact]
    public void CompanionPreferences_RedactOnAnyParent_AndInventNoReplacementReading()
    {
        var a = Guid.NewGuid();
        var p = new CompanionPreference
        {
            Id = Guid.NewGuid(), UserId = User, Subject = "sheds", Affinity = 0.8,
            Confidence = 0.6, Reason = "he lights up about it", Observations = 3,
            CreatedAt = Now, UpdatedAt = Now, Embedding = [0.5f],
            EvidenceMessageIdsJson = EvidenceForgetting.WriteIds([a, Guid.NewGuid()]),
        };

        Assert.Equal(1, EvidenceForgetting.ForgetCompanionPreferences([p], [a]));

        Assert.Equal(string.Empty, p.Subject);
        Assert.Null(p.Reason);
        Assert.Null(p.Embedding);
        Assert.True(p.EvidenceForgotten);
        // Affinity is NOT reset to neutral: inventing a reading would be worse than none,
        // and the flag is what stops it contributing.
        Assert.Equal(0.8, p.Affinity);
    }

    [Fact]
    public void TurnRecords_RedactEveryDerivedColumn_AndKeepTheMetrics()
    {
        var msg = Guid.NewGuid();
        var r = new TurnRecord
        {
            Id = Guid.NewGuid(), UserId = User, Timestamp = Now, SourceMessageId = msg,
            UserPreview = SameWords, AssistantPreview = "I remember.",
            RetrievalQuery = SameWords, Retrieved = "[...]", Plan = "{...}",
            FocalTerms = "shed", BoundQuestion = "which shed?", ResolvedReference = "the shed",
            Decisions = "privacy=not-sensitive", PacketTokens = 512, ModelUsed = "stheno",
            Intent = "answer", IntentConfidence = 0.9,
        };

        Assert.Equal(1, EvidenceForgetting.ForgetTurnRecords([r], [msg]));

        Assert.Null(r.UserPreview);
        Assert.Null(r.AssistantPreview);
        Assert.Null(r.RetrievalQuery);
        Assert.Null(r.Retrieved);
        Assert.Null(r.Plan);
        Assert.Null(r.FocalTerms);
        Assert.Null(r.BoundQuestion);
        Assert.Null(r.ResolvedReference);
        // Content-free diagnostics survive, which is the whole point of redacting not deleting.
        Assert.Equal(512, r.PacketTokens);
        Assert.Equal("stheno", r.ModelUsed);
        Assert.Equal("privacy=not-sensitive", r.Decisions);

        Assert.Equal(0, EvidenceForgetting.ForgetTurnRecords([r], [msg]));
    }

    [Fact]
    public void MalformedLineage_IsTreatedAsNoneRatherThanAttributed()
    {
        var r = new Reflection
        {
            Id = Guid.NewGuid(), UserId = User, CreatedAt = Now, Musing = "something",
            SourceMessageIdsJson = "not json at all",
        };

        // Unforgettable-by-identity is the safe failure. Falsely attributing it to whichever
        // message is being forgotten would delete on a guess.
        Assert.Equal(0, EvidenceForgetting.ForgetReflections([r], [Guid.NewGuid()], out _));
        Assert.Equal("something", r.Musing);
    }

    // ---- the stores: user isolation is structural -------------------------------------------

    [Fact]
    public async Task OneUsersForget_CannotReachAnothers_EvenWithTheSameMessageId()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var shared = Guid.NewGuid();          // deliberately the SAME id for both users
        db.Experiences.AddRange(
            new Experience { Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat",
                             Kind = "said", Text = SameWords, EvidenceMessageId = shared },
            new Experience { Id = Guid.NewGuid(), UserId = Other, At = Now, Source = "chat",
                             Kind = "said", Text = SameWords, EvidenceMessageId = shared });
        await db.SaveChangesAsync();

        var store = sp.GetRequiredService<IExperienceStore>();
        Assert.Equal(1, await store.ForgetByEvidenceAsync(User, [shared], Now));

        var mine = await db.Experiences.AsNoTracking().SingleAsync(e => e.UserId == User);
        var theirs = await db.Experiences.AsNoTracking().SingleAsync(e => e.UserId == Other);
        Assert.True(mine.EvidenceForgotten);
        Assert.False(theirs.EvidenceForgotten);
        Assert.Equal(SameWords, theirs.Text);
    }

    [Fact]
    public async Task ForgettingSurvivesRestart_AndStaysIdempotent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"a1-restart-{Guid.NewGuid():N}.db");
        var msg = Guid.NewGuid();
        try
        {
            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                using var scope = host.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
                db.Experiences.Add(new Experience
                {
                    Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat",
                    Kind = "said", Text = SameWords, EvidenceMessageId = msg,
                });
                await db.SaveChangesAsync();

                Assert.Equal(1, await scope.ServiceProvider
                    .GetRequiredService<IExperienceStore>()
                    .ForgetByEvidenceAsync(User, [msg], Now));
            }

            await using (var host = new TestHost(Now, connectionString: $"Data Source={dbPath}"))
            {
                using var scope = host.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();
                var row = await db.Experiences.AsNoTracking().SingleAsync();

                Assert.True(row.EvidenceForgotten);
                Assert.Equal(string.Empty, row.Text);
                Assert.Equal(0, await scope.ServiceProvider
                    .GetRequiredService<IExperienceStore>()
                    .ForgetByEvidenceAsync(User, [msg], Now));
            }
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }

    // ---- the real /forget path ----------------------------------------------------------------

    [Fact]
    public async Task TheRealForgetPath_ReachesEveryDerivedStore()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var conversation = await sp.GetRequiredService<IConversationStore>()
            .StartConversationAsync(User, "t", "mock", "test");
        var message = new Message
        {
            Id = Guid.NewGuid(), UserId = User, ConversationId = conversation.Id,
            Role = MessageRole.User, Content = SameWords, Timestamp = Now,
        };
        await sp.GetRequiredService<IConversationStore>().AddMessageAsync(message);

        var experience = new Experience
        {
            Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat", Kind = "said",
            Text = SameWords, EvidenceMessageId = message.Id,
        };
        var reflection = new Reflection
        {
            Id = Guid.NewGuid(), UserId = User, CreatedAt = Now, Musing = "He mentions the shed.",
            SourceMessageIdsJson = EvidenceForgetting.WriteIds([message.Id]),
        };
        db.Experiences.Add(experience);
        db.Reflections.Add(reflection);
        db.Curiosities.Add(new Curiosity
        {
            Id = Guid.NewGuid(), UserId = User, ReflectionId = reflection.Id,
            Question = "Shed?", Status = CuriosityStatus.Open, CreatedAt = Now,
        });
        db.AttentionItems.Add(new AttentionItem
        {
            Id = Guid.NewGuid(), UserId = User, Subject = "shed", Summary = SameWords,
            SourceType = AttentionSourceType.Conversation, SourceId = message.Id.ToString(),
            Owner = MemoryOwner.Shared, Strength = 0.4, CreatedAt = Now,
            LastActivatedAt = Now, ExpiresAt = Now.AddDays(7), Status = AttentionStatus.Active,
        });
        db.CompanionPreferences.Add(new CompanionPreference
        {
            Id = Guid.NewGuid(), UserId = User, Subject = "sheds", Affinity = 0.7,
            Confidence = 0.5, Reason = "he brings it up", Observations = 1,
            CreatedAt = Now, UpdatedAt = Now,
            EvidenceMessageIdsJson = EvidenceForgetting.WriteIds([message.Id]),
        });
        db.SharedExperiencePerspectives.Add(new SharedExperiencePerspective
        {
            Id = Guid.NewGuid(), UserId = User, ExperienceId = experience.Id,
            Owner = MemoryOwner.Shared, Summary = SameWords, Confidence = 0.5,
            Evidence = SameWords, CreatedAt = Now,
        });
        db.KnowledgeGaps.Add(new KnowledgeGap
        {
            Id = Guid.NewGuid(), UserId = User, Kind = GapKind.UnknownConcept, Subject = "quokka",
            Source = GapSource.KnowledgeLookup, Occurrences = 1, FirstSeen = Now, LastSeen = Now,
            EvidenceMessageIdsJson = EvidenceForgetting.WriteIds([message.Id]),
        });
        db.TurnRecords.Add(new TurnRecord
        {
            Id = Guid.NewGuid(), UserId = User, Timestamp = Now, SourceMessageId = message.Id,
            UserPreview = SameWords, Decisions = "privacy=not-sensitive", PacketTokens = 10,
        });
        await db.SaveChangesAsync();

        var memoryId = Guid.NewGuid();
        var memories = sp.GetRequiredService<IMemoryStore>();
        await memories.AddSemanticAsync(new SemanticMemory
        {
            Id = memoryId, UserId = User, Subject = "user", Predicate = "owns",
            Value = "a shed", NormalizedFact = "The user owns a shed.",
            FirstObserved = Now, LastConfirmed = Now, CreatedAt = Now,
        });
        await memories.AddEvidenceAsync(User,
        [
            new MemoryEvidence
            {
                Id = Guid.NewGuid(), UserId = User, MemoryId = memoryId,
                MemoryKind = MemoryKind.Semantic, MessageId = message.Id, Excerpt = SameWords,
            },
        ]);

        Assert.True(await sp.GetRequiredService<IMemoryCurator>()
            .ForgetAsync(User, memoryId, "user asked to forget"));

        // Every one of the eight, through the real orchestrator.
        Assert.True((await db.Experiences.AsNoTracking().SingleAsync()).EvidenceForgotten);
        Assert.Null((await db.Reflections.AsNoTracking().SingleAsync()).Musing);
        Assert.Equal(CuriosityStatus.EvidenceForgotten,
            (await db.Curiosities.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.AttentionItems.AsNoTracking().ToListAsync());
        Assert.True((await db.CompanionPreferences.AsNoTracking().SingleAsync()).EvidenceForgotten);
        Assert.Empty(await db.SharedExperiencePerspectives.AsNoTracking().ToListAsync());
        Assert.Equal(GapStatus.EvidenceForgotten,
            (await db.KnowledgeGaps.AsNoTracking().SingleAsync()).Status);
        Assert.Null((await db.TurnRecords.AsNoTracking().SingleAsync()).UserPreview);
    }

    [Fact]
    public async Task ForgottenContent_IsAbsentFromEveryDerivedTextColumn()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Infrastructure.Persistence.CompanionDbContext>();

        var msg = Guid.NewGuid();
        db.Experiences.Add(new Experience
        {
            Id = Guid.NewGuid(), UserId = User, At = Now, Source = "chat", Kind = "said",
            Text = SameWords, EvidenceMessageId = msg,
        });
        db.TurnRecords.Add(new TurnRecord
        {
            Id = Guid.NewGuid(), UserId = User, Timestamp = Now, SourceMessageId = msg,
            UserPreview = SameWords, RetrievalQuery = SameWords, Decisions = "",
        });
        await db.SaveChangesAsync();

        await sp.GetRequiredService<IExperienceStore>().ForgetByEvidenceAsync(User, [msg], Now);
        await sp.GetRequiredService<IDiagnosticsStore>().ForgetByEvidenceAsync(User, [msg], Now);

        var haystack = string.Join("\n",
            (await db.Experiences.AsNoTracking().ToListAsync()).Select(e => e.Text)
                .Concat((await db.TurnRecords.AsNoTracking().ToListAsync())
                    .SelectMany(t => new[] { t.UserPreview, t.RetrievalQuery, t.Retrieved, t.Plan })
                    .Where(x => x is not null)!));

        Assert.DoesNotContain(SameWords, haystack, StringComparison.OrdinalIgnoreCase);
    }
}
