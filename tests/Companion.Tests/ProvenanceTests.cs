using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ProvenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EverySeededMemory_HasEvidence_TraceableToARealMessage()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<CompanionSeeder>();
        await seeder.SeedAsync(Now);

        var memories = scope.ServiceProvider.GetRequiredService<IMemoryStore>();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        var all = await memories.GetRetrievableMemoriesAsync(CompanionSeeder.DemoUserId);
        Assert.NotEmpty(all);

        foreach (var memory in all)
        {
            var evidence = await memories.GetEvidenceAsync(memory.Id);
            Assert.NotEmpty(evidence); // no memory without provenance

            foreach (var e in evidence)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Excerpt));
                var source = await conversations.GetMessageAsync(e.MessageId, CompanionSeeder.DemoUserId);
                Assert.NotNull(source); // the cited message actually exists
            }
        }
    }
}
