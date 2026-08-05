using Companion.Core.Abstractions;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class StreamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Collector : IProgress<string>
    {
        public List<string> Chunks { get; } = new();
        public void Report(string value) => Chunks.Add(value);
    }

    [Fact]
    public async Task StreamedReply_Concatenates_ToTheFinalResponse()
    {
        await using var host = new TestHost(Now);
        Guid conversationId;
        using (var scope = host.CreateScope())
        {
            var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");
            conversationId = conv.Id;
        }

        var collector = new Collector();
        using var turnScope = host.CreateScope();
        var companion = turnScope.ServiceProvider.GetRequiredService<ICompanion>();
        var trace = await companion.RespondAsync(
            CompanionSeeder.DemoUserId, conversationId, "hello there companion", collector);

        Assert.True(collector.Chunks.Count > 1);                 // actually streamed in pieces
        Assert.Equal(trace.Response, string.Concat(collector.Chunks)); // pieces reconstruct the reply
    }

    [Fact]
    public async Task NonStreaming_StillWorks_WhenNoSinkProvided()
    {
        await using var host = new TestHost(Now);
        using var scope = host.CreateScope();
        var conv = await scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test");
        var companion = scope.ServiceProvider.GetRequiredService<ICompanion>();

        var trace = await companion.RespondAsync(CompanionSeeder.DemoUserId, conv.Id, "hi");
        Assert.False(string.IsNullOrWhiteSpace(trace.Response));
    }
}
