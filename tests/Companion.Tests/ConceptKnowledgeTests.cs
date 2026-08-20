using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Persistence;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Phase 3 (docs/CONCEPT_KNOWLEDGE.md): the epistemic ownership boundary. The language
/// model may understand a concept without Ava claiming to know it; Ava-owned knowledge
/// exists only with user-taught evidence and provenance. The teaching detector is
/// deliberately high-precision — every negative here contains "axe is" and must not teach,
/// because a missed teaching costs a corpus row while a false positive permanently stores
/// an accidental remark as world knowledge.
/// </summary>
public class ConceptKnowledgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "concept-user";
    private const string AxeTeaching = "An axe is a tool used for chopping or splitting wood.";

    // ---- the detector: high precision over recall ----

    [Theory]
    [InlineData(AxeTeaching, "axe")]
    [InlineData("Gravity is a force that pulls objects toward each other.", "Gravity")]
    [InlineData("Disney World is a theme park resort in Florida.", "Disney World")]
    [InlineData("A quokka means a small wallaby native to Western Australia.", "quokka")]
    public void ExplicitDefinitions_Teach(string message, string expectedTerm)
    {
        var teaching = TeachingDetector.Detect(message);
        Assert.NotNull(teaching);
        Assert.Equal(expectedTerm, teaching!.Term);
    }

    [Theory]
    [InlineData("An axe is sitting in my garage.")]        // progressive + personal — a remark
    [InlineData("An axe is probably what I need.")]        // adverb-led, first person
    [InlineData("An axe is expensive.")]                   // bare adjective, no category phrase
    [InlineData("My axe is dull.")]                        // possessive subject — biography
    [InlineData("That axe is the sharpest one I own.")]    // demonstrative subject
    [InlineData("An axe was a tool people used daily.")]   // past tense — narrative
    [InlineData("Is an axe a tool for chopping wood?")]    // a question teaches nothing
    [InlineData("Dinner is a nightmare tonight.")]         // temporal anchor — a remark about now
    public void NonDefinitionalAxeIsSentences_DoNotTeach(string message)
        => Assert.Null(TeachingDetector.Detect(message));

    [Fact]
    public void RejectedLooseShapes_AreTheCapturePopulation()
    {
        // "An axe is expensive" is not teaching — but it IS loose-copular, so it lands in
        // the knowledge.teaching capture as a labeled negative for the future corpus.
        Assert.True(TeachingDetector.LooseShape("An axe is expensive."));
        Assert.Null(TeachingDetector.Detect("An axe is expensive."));
        Assert.False(TeachingDetector.LooseShape("I sharpened the blade this morning."));
    }

    // ---- learning: evidence-bound, user-only, supersession-with-history ----

    private static async Task<TestHost> HostAsync(Action<Core.CompanionOptions>? opts = null)
    {
        var host = new TestHost(Now, configureOptions: opts);
        using var scope = host.CreateScope();
        await Task.CompletedTask;
        return host;
    }

    private static Message UserMsg(string content) => new()
    {
        Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), UserId = UserId,
        Role = MessageRole.User, Content = content, Timestamp = Now,
    };

    [Fact]
    public async Task Teaching_CreatesAvaOwnedKnowledge_WithUserAuthoredEvidence()
    {
        await using var host = await HostAsync();
        using var scope = host.CreateScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<IConceptKnowledge>();
        var message = UserMsg(AxeTeaching);

        var taught = await knowledge.LearnFromAsync(UserId, message);

        Assert.Equal("axe", taught);
        var lookup = await knowledge.LookupAsync(UserId, "axe");
        Assert.Equal(ConceptFamiliarity.Known, lookup.Familiarity);
        Assert.Equal(AxeTeaching, lookup.Definition);
        Assert.Equal(KnowledgeOrigin.Taught, lookup.Origin);

        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var assertion = Assert.Single(db.ConceptAssertions.ToList());
        Assert.Equal(MemoryOwner.Companion, ((IMemory)assertion).Owner);
        Assert.Equal(MemoryKind.Concept, ((IMemory)assertion).Kind);
        var evidence = Assert.Single(db.Evidence.Where(e => e.MemoryId == assertion.Id).ToList());
        Assert.Equal(message.Id, evidence.MessageId);         // provenance to the utterance
        Assert.Equal(AxeTeaching, evidence.Excerpt);
        Assert.Single(db.Revisions.Where(r => r.MemoryId == assertion.Id).ToList());
    }

    [Fact]
    public async Task AnAssistantMessage_CanNeverTeach()
    {
        // The laundering barrier: whatever the model's reply explains, it is not hers.
        await using var host = await HostAsync();
        using var scope = host.CreateScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<IConceptKnowledge>();
        var reply = UserMsg("A quokka is a small wallaby native to Western Australia.");
        reply.Role = MessageRole.Assistant;

        Assert.Null(await knowledge.LearnFromAsync(UserId, reply));
        Assert.Equal(ConceptFamiliarity.Unknown, (await knowledge.LookupAsync(UserId, "quokka")).Familiarity);
    }

    [Fact]
    public async Task ReTeaching_SupersedesWithHistory()
    {
        await using var host = await HostAsync();
        using var scope = host.CreateScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<IConceptKnowledge>();

        await knowledge.LearnFromAsync(UserId, UserMsg(AxeTeaching));
        await knowledge.LearnFromAsync(UserId,
            UserMsg("An axe is a bladed tool with a weighted head on a wooden handle."));

        var db = scope.ServiceProvider.GetRequiredService<CompanionDbContext>();
        var assertions = db.ConceptAssertions.ToList();
        Assert.Equal(2, assertions.Count);
        var old = Assert.Single(assertions, a => a.Status == MemoryStatus.Superseded);
        var current = Assert.Single(assertions, a => a.Status == MemoryStatus.Active);
        Assert.Equal(current.Id, old.SupersededById);

        Assert.Contains("bladed tool", (await knowledge.LookupAsync(UserId, "axe")).Definition);
    }

    [Fact]
    public async Task NonTeachingRemarks_MintNothing()
    {
        await using var host = await HostAsync();
        using var scope = host.CreateScope();
        var knowledge = scope.ServiceProvider.GetRequiredService<IConceptKnowledge>();

        Assert.Null(await knowledge.LearnFromAsync(UserId, UserMsg("My axe is dull.")));
        Assert.Null(await knowledge.LearnFromAsync(UserId, UserMsg("An axe is sitting in my garage.")));

        Assert.Empty(scope.ServiceProvider.GetRequiredService<CompanionDbContext>().Concepts.ToList());
    }

    // ---- the epistemic question, end to end ----

    private static async Task<(TestHost host, Guid conv)> SessionAsync(bool promote)
    {
        var host = new TestHost(Now, configureOptions: o => o.PromoteKnowledgeBoundary = promote);
        using var scope = host.CreateScope();
        var conv = (await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(UserId, "t", "mock", "test")).Id;
        return (host, conv);
    }

    private static async Task<TurnTrace> SayAsync(TestHost host, Guid conv, string message)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICompanion>()
            .RespondAsync(UserId, conv, message);
    }

    [Fact]
    public async Task TaughtConcept_AnswersFromHerStore_WithProvenance()
    {
        var (host, conv) = await SessionAsync(promote: true);
        await using var _ = host;

        await SayAsync(host, conv, AxeTeaching);
        var turn1 = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("axe", turn1.Decisions.Single(d => d.Stage == "knowledge.taught").Verdict);

        var trace = await SayAsync(host, conv, "Do you know what an axe is?");
        var turn2 = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("axe:known", turn2.Decisions.Single(d => d.Stage == "knowledge.lookup").Verdict);
        Assert.Equal("known-injected", turn2.Decisions.Single(d => d.Stage == "knowledge.promotion").Verdict);

        var rendered = trace.Packet.Render();
        Assert.Contains("You HAVE learned", rendered);
        Assert.Contains("chopping or splitting wood", rendered);
        Assert.Contains("taught you", rendered);
    }

    [Fact]
    public async Task UnknownConcept_IsHonestlyNotLearned_WhateverTheModelUnderstands()
    {
        var (host, conv) = await SessionAsync(promote: true);
        await using var _ = host;

        var trace = await SayAsync(host, conv, "Do you know what a quokka is?");

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("quokka:unknown", turn.Decisions.Single(d => d.Stage == "knowledge.lookup").Verdict);
        Assert.Contains("You have NOT learned", trace.Packet.Render());

        // And the model's reply — whatever it said about quokkas — taught nothing.
        using var verify = host.CreateScope();
        Assert.Empty(verify.ServiceProvider.GetRequiredService<CompanionDbContext>().Concepts.ToList());
    }

    [Fact]
    public async Task WithTheFlagOff_TheLookupIsRecorded_ButNothingIsInjected()
    {
        var (host, conv) = await SessionAsync(promote: false);
        await using var _ = host;

        var trace = await SayAsync(host, conv, "Do you know what a quokka is?");

        var turn = host.Services.GetRequiredService<ITurnTraceLog>().Recent(UserId, 1).Single();
        Assert.Equal("quokka:unknown", turn.Decisions.Single(d => d.Stage == "knowledge.lookup").Verdict);
        Assert.DoesNotContain(turn.Decisions, d => d.Stage == "knowledge.promotion");
        Assert.DoesNotContain("You have NOT learned", trace.Packet.Render());
    }

    [Fact]
    public async Task LearnedKnowledge_ReachesThePacket_InItsOwnSection()
    {
        var (host, conv) = await SessionAsync(promote: false);
        await using var _ = host;

        await SayAsync(host, conv, AxeTeaching);
        var trace = await SayAsync(host, conv, "I need to split some wood for the stove with an axe.");

        var rendered = trace.Packet.Render();
        Assert.Contains("What you (Ava) have learned about the world", rendered);
        Assert.Contains("taught you this on", rendered);
    }

    [Theory]
    [InlineData("Do you know what an axe is?", "axe")]
    [InlineData("What do you know about gravity?", "gravity")]
    [InlineData("Have I taught you about tides?", "tides")]
    public void KnowledgeQuestions_AreDetected(string message, string term)
        => Assert.Equal(term, KnowledgeQuestionDetector.Detect(message)?.ToLowerInvariant());

    [Theory]
    [InlineData("What should I cook for her?")]      // conversational, not epistemic
    [InlineData("Do you know what her favorite axe is?")] // pronoun — working context's job
    [InlineData("What is the weather like?")]
    public void OrdinaryQuestions_AreNotKnowledgeQuestions(string message)
        => Assert.Null(KnowledgeQuestionDetector.Detect(message));
}
