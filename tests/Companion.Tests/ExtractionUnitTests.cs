using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

public class ConfidenceCalculatorTests
{
    [Fact]
    public void DirectUserStatement_IsMoreConfidentThanInference()
    {
        var direct = ConfidenceCalculator.Compute(0.5, fromDirectUserStatement: true, corroborations: 0);
        var inferred = ConfidenceCalculator.Compute(0.5, fromDirectUserStatement: false, corroborations: 0);
        Assert.True(direct > inferred);
    }

    [Fact]
    public void Corroboration_RaisesConfidence_WithDiminishingReturns()
    {
        var one = ConfidenceCalculator.Compute(0.5, true, corroborations: 1);
        var many = ConfidenceCalculator.Compute(0.5, true, corroborations: 10);
        Assert.True(many > one);
        Assert.True(many <= 1.0); // capped
    }

    [Fact]
    public void Confidence_IsClampedToUnitInterval()
    {
        Assert.Equal(1.0, ConfidenceCalculator.Compute(2.0, true, 10));
        Assert.Equal(0.0, ConfidenceCalculator.Compute(-1.0, false, 0));
    }
}

public class MemoryNormalizerTests
{
    [Fact]
    public void Normalize_CollapsesWhitespace_AndLowercasesSlot()
    {
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "  User ",
            Predicate = "Prefers",
            Value = "Spicy   Thai  food",
            Content = "  The user   prefers spicy Thai food.  ",
        };

        var n = MemoryNormalizer.Normalize(candidate);

        Assert.Equal("user", n.Subject);
        Assert.Equal("prefers", n.Predicate);
        Assert.Equal("Spicy Thai food", n.Value);
        Assert.Equal("The user prefers spicy Thai food.", n.Content);
    }

    [Fact]
    public void Normalize_DerivesContent_WhenMissing()
    {
        var candidate = new MemoryCandidate
        {
            Kind = MemoryKind.Semantic,
            Subject = "user",
            Predicate = "uses",
            Value = "a Jetson Nano",
            Content = "",
        };

        var n = MemoryNormalizer.Normalize(candidate);
        Assert.Equal("user uses a Jetson Nano", n.Content);
    }

    [Fact]
    public void SlotKey_IgnoresValue_ButValueKeyDoesNot()
    {
        var slotA = MemoryNormalizer.SemanticSlotKey("user", "diet");
        var slotB = MemoryNormalizer.SemanticSlotKey("user", "diet");
        Assert.Equal(slotA, slotB);

        var valA = MemoryNormalizer.SemanticValueKey("user", "diet", "low carb");
        var valB = MemoryNormalizer.SemanticValueKey("user", "diet", "keto");
        Assert.NotEqual(valA, valB);
    }
}
