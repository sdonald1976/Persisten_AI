using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The idle sleep cycle: think first, then tidy. Consolidation runs only when reflection actually
/// processed new material; stale curiosities are let go on every cycle regardless.
/// </summary>
public class SleepCycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const string User = CompanionSeeder.DemoUserId;

    private sealed class FakeReflector : IReflector
    {
        private readonly ReflectionResult? _result;
        public FakeReflector(ReflectionResult? result) => _result = result;
        public Task<ReflectionResult?> ReflectAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(_result);
    }

    private sealed class CountingConsolidator : IMemoryConsolidator
    {
        public int Calls { get; private set; }
        public Task<ConsolidationResult> ConsolidateAsync(string userId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ConsolidationResult { Created = new List<ConsolidatedGroup>() });
        }
    }

    private static SleepCycle BuildCycle(
        IServiceProvider sp, ReflectionResult? reflection, CountingConsolidator consolidator)
        => new(
            new FakeReflector(reflection),
            consolidator,
            sp.GetRequiredService<IReflectionStore>(),
            sp.GetRequiredService<IAnticipationStore>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<SleepCycle>.Instance);

    private static ReflectionResult SomeReflection() => new()
    {
        Reflection = new Reflection { UserId = User, CreatedAt = Now, CoveredThrough = Now, Musing = "a thought" },
    };

    [Fact]
    public async Task AfterARealReflection_ConsolidationRuns()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var consolidator = new CountingConsolidator();

        var result = await BuildCycle(scope.ServiceProvider, SomeReflection(), consolidator).RunAsync(User);

        Assert.Equal(1, consolidator.Calls);
        Assert.NotNull(result.Reflection);
    }

    [Fact]
    public async Task WithNothingNewToThinkAbout_ConsolidationIsSkipped()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var consolidator = new CountingConsolidator();

        var result = await BuildCycle(scope.ServiceProvider, reflection: null, consolidator).RunAsync(User);

        // No new material = the memory landscape hasn't changed = nothing to roll up.
        Assert.Equal(0, consolidator.Calls);
        Assert.Null(result.Reflection);
    }

    [Fact]
    public async Task StaleCuriosities_AreLetGo_FreshOnesAreKept()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var sp = scope.ServiceProvider;
        var reflections = sp.GetRequiredService<IReflectionStore>();
        await reflections.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now.AddDays(-20), CoveredThrough = Now.AddDays(-20) },
            new[]
            {
                new Curiosity { UserId = User, Question = "Stale?", Status = CuriosityStatus.Open, CreatedAt = Now.AddDays(-20) },
            });
        await reflections.AddAsync(
            new Reflection { UserId = User, CreatedAt = Now.AddDays(-1), CoveredThrough = Now.AddDays(-1) },
            new[]
            {
                new Curiosity { UserId = User, Question = "Fresh?", Status = CuriosityStatus.Open, CreatedAt = Now.AddDays(-1) },
            });

        var result = await BuildCycle(sp, reflection: null, new CountingConsolidator()).RunAsync(User);

        Assert.Equal(1, result.StaleCuriositiesDismissed);
        var open = Assert.Single(await reflections.GetOpenCuriositiesAsync(User));
        Assert.Equal("Fresh?", open.Question); // the stale one is Dismissed, not deleted
    }
}
