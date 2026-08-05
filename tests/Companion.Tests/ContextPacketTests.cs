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
