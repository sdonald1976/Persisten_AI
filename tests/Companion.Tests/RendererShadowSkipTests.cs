using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Renderer;
using Companion.Infrastructure.Seeding;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The privacy-sensitive skip, pinned.
///
/// Phase-A hardening proposed a structurally-redacted row for sensitive turns. It was
/// declined: a row is keyed to a turn and carries a timestamp, so its mere existence plus a
/// frame transition recovers "a private turn at 22:14 entered a fiction scene" — and
/// sensitive turns are exactly where metadata inference matters most.
///
/// The measured cost is in docs/RENDERER_SHADOW.md §1.1. This test exists so the boundary
/// cannot erode quietly the next time collection volume looks tempting.
/// </summary>
public class RendererShadowSkipTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task APrivacySensitiveTurn_WritesNoShadowRowOfAnyKind()
    {
        var recorder = new CollectingRecorder();
        await using var host = new TestHost(
            Now,
            configureServices: s => s.AddSingleton<IShadowRecorder>(recorder),
            settings: new Dictionary<string, string?>
            {
                ["Companion:RendererShadow:Enabled"] = "true",
                ["Companion:RendererShadow:Endpoint"] = "http://127.0.0.1:59993",
                ["Companion:RendererShadow:TimeoutSeconds"] = "5",
            });

        Guid conversationId;
        using (var seed = host.CreateScope())
            conversationId = (await seed.ServiceProvider.GetRequiredService<IConversationStore>()
                .StartConversationAsync(CompanionSeeder.DemoUserId, "t", "mock", "test")).Id;

        using (var scope = host.CreateScope())
        {
            var trace = await scope.ServiceProvider.GetRequiredService<ICompanion>().RespondAsync(
                CompanionSeeder.DemoUserId, conversationId,
                // An explicit privacy phrase — what the rule-based classifier keys on.
                "Keep this private: the antidepressants are making my insomnia worse.");
            Assert.Equal(TurnStatus.Answered, trace.Status);
        }

        var service = (RendererShadowService)host.Services.GetRequiredService<IRendererShadow>();
        await service.DisposeAsync();

        // Not a redacted row, not a structural row, not a plan-only row. None.
        Assert.DoesNotContain(recorder.Rows, r => r.Subject == RendererShadowService.RendererV3Subject);
        Assert.DoesNotContain(recorder.Rows, r => r.Subject == RendererShadowService.RendererShadowSubject);
    }

    private sealed class CollectingRecorder : IShadowRecorder
    {
        public List<ShadowComparison> Rows { get; } = [];
        public bool IsRecording => true;
        public bool IsShadowing => true;
        public Task RecordAsync(ShadowComparison c, CancellationToken ct = default)
        { Rows.Add(c); return Task.CompletedTask; }
        public Task<IReadOnlyList<ShadowAgreement>> GetAgreementAsync(DateTimeOffset s, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowAgreement>>([]);
        public Task<IReadOnlyList<ShadowComparison>> GetDisagreementsAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>(Rows);
        public Task<IReadOnlyList<ShadowComparison>> GetCapturesAsync(string? s, int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ShadowComparison>>([]);
        public Task<int> PruneAsync(DateTimeOffset o, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ForgetCapturesAsync(IReadOnlyCollection<string> e, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
