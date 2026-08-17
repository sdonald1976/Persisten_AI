using Companion.Core;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Companion.Tests;

public class ContextPacketTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ContextAssembler Assembler(int budget = 800) =>
        new(Options.Create(new CompanionOptions { MemoryTokenBudget = budget }));

    private static RetrievalResult Result(IMemory memory) => new()
    {
        Memory = memory,
        Score = 1.0,
        Signals = new Dictionary<string, double> { ["test"] = 1.0 },
        Reason = "test",
    };

    private static SemanticMemory Fact(string text, double confidence = 0.9,
        Validity validity = Validity.Current, MemoryStatus status = MemoryStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        UserId = "u",
        NormalizedFact = text,
        Confidence = confidence,
        Validity = validity,
        Status = status,
        LastConfirmed = Now,
        CreatedAt = Now,
    };

    [Fact]
    public void HighConfidenceCurrentFact_IsLabeledDirect()
    {
        var packet = Assembler().Assemble("hi", Array.Empty<Message>(), new[] { Result(Fact("A", confidence: 0.9)) }, ProjectContext.Empty);
        Assert.Equal(ContextProvenance.DirectStatement, packet.Memories.Single().Provenance);
    }

    [Fact]
    public void LowConfidenceFact_IsLabeledInferred()
    {
        var packet = Assembler().Assemble("hi", Array.Empty<Message>(), new[] { Result(Fact("A", confidence: 0.3)) }, ProjectContext.Empty);
        Assert.Equal(ContextProvenance.Inferred, packet.Memories.Single().Provenance);
    }

    [Fact]
    public void HistoricalFact_IsLabeledOutdated_AndProducesUncertaintyNote()
    {
        var packet = Assembler().Assemble(
            "hi", Array.Empty<Message>(), new[] { Result(Fact("Old device", validity: Validity.Historical)) }, ProjectContext.Empty);

        Assert.Equal(ContextProvenance.Outdated, packet.Memories.Single().Provenance);
        Assert.NotEmpty(packet.UncertaintyNotes);
    }

    [Fact]
    public void SupersededMemory_IsLabeledOutdated()
    {
        var packet = Assembler().Assemble(
            "hi", Array.Empty<Message>(), new[] { Result(Fact("Replaced", status: MemoryStatus.Superseded)) }, ProjectContext.Empty);
        Assert.Equal(ContextProvenance.Outdated, packet.Memories.Single().Provenance);
    }

    /// <summary>
    /// A memory the user has said is wrong must never be handed to the model as something they
    /// told you. There was no branch for it, so it fell through to the confidence test and came out
    /// DirectStatement: she said "I've flagged that as disputed and won't rely on it" and then
    /// asserted it two turns later, off the back of its own packet entry.
    /// </summary>
    [Fact]
    public void DisputedMemory_IsLabeledDisputed_NotDirect()
    {
        var packet = Assembler().Assemble(
            "hi", Array.Empty<Message>(),
            new[] { Result(Fact("Wrong thing", confidence: 0.9, status: MemoryStatus.Disputed)) },
            ProjectContext.Empty);

        Assert.Equal(ContextProvenance.Disputed, packet.Memories.Single().Provenance);
    }

    /// <summary>
    /// And it must be rendered under a heading that says so — the label is only worth having if it
    /// reaches the prompt.
    /// </summary>
    [Fact]
    public void DisputedMemory_IsRenderedUnderItsOwnWarning()
    {
        var packet = Assembler().Assemble(
            "hi", Array.Empty<Message>(),
            new[] { Result(Fact("The irrigation is at the allotment", status: MemoryStatus.Disputed)) },
            ProjectContext.Empty);

        var rendered = ContextPacketRenderer.Build(packet).Text;

        Assert.Contains("WRONG", rendered);
        Assert.Contains("The irrigation is at the allotment", rendered);
        Assert.DoesNotContain("## What the user has told you", rendered);
    }

    [Fact]
    public void TokenBudget_IsRespected()
    {
        // Each fact ~ 100 chars => ~25 tokens. Budget of 30 admits only the first.
        var big = new string('x', 100);
        var results = new[] { Result(Fact(big)), Result(Fact(big)), Result(Fact(big)) };

        var packet = Assembler(budget: 30).Assemble("hi", Array.Empty<Message>(), results, ProjectContext.Empty);

        Assert.Single(packet.Memories);
    }

    [Fact]
    public void RenderedPacket_SeparatesProvenanceSections()
    {
        var results = new[]
        {
            Result(Fact("A direct current fact", confidence: 0.9)),
            Result(Fact("An old fact", validity: Validity.Historical)),
        };
        var text = Assembler().Assemble("hi", Array.Empty<Message>(), results, ProjectContext.Empty).Render();

        Assert.Contains("direct", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outdated", text, StringComparison.OrdinalIgnoreCase);
    }
}
