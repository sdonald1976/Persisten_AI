namespace Companion.Tests.Fixtures;

/// <summary>A deterministic clock so time-dependent behavior (recency) is reproducible in tests.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
