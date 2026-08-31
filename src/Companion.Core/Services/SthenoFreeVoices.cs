using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

/// <summary>
/// The Stheno-free route's greeting seat. For the routed user, the greeting IS the
/// deterministic Greeter's output - elapsed time and remembered threads from typed state,
/// nothing invented - and the "rephrase it in her voice" upgrade is a no-op, because the only
/// voice model available for that today is the conversational model this route exists to keep
/// out. Everyone else keeps the model-voiced greeting unchanged.
///
/// This closes the hole the failed acceptance test walked through: the greeting was the one
/// reply-shaped text still authored by Stheno on the route, and it opened the conversation by
/// inventing interactions and accomplishments the system never had.
/// </summary>
public sealed class SthenoFreeGreeter(
    IGreeter inner,
    IGreetingRephraser? innerRephraser,
    Greeter deterministic,
    IOptions<CompanionOptions> options) : IGreeter, IGreetingRephraser
{
    private readonly SthenoFreeOptions _route = options.Value.SthenoFree;

    public Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
        => _route.AppliesTo(userId)
            ? deterministic.GreetAsync(userId, ct)
            : inner.GreetAsync(userId, ct);

    public Task<Greeting> RephraseAsync(
        Greeting grounded, string? userId = null, CancellationToken ct = default)
        => userId is not null && _route.AppliesTo(userId)
            ? Task.FromResult(grounded)
            : innerRephraser?.RephraseAsync(grounded, userId, ct) ?? Task.FromResult(grounded);
}

/// <summary>
/// Outreach restyling, route-aware: the deterministic draft (already typed-state-grounded) IS
/// the message for the routed user; the conversational model never rewords it.
/// </summary>
public sealed class SthenoFreeVoiceRephraser(
    IVoiceRephraser inner, IOptions<CompanionOptions> options) : IVoiceRephraser
{
    private readonly SthenoFreeOptions _route = options.Value.SthenoFree;

    public Task<string> RephraseAsync(
        string userId, string draft, string situation, CancellationToken ct = default)
        => _route.AppliesTo(userId)
            ? Task.FromResult(draft)
            : inner.RephraseAsync(userId, draft, situation, ct);
}
