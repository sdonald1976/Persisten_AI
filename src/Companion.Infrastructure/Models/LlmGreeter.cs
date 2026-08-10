using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Writes the session opener with the language model instead of a fixed template, so the greeting
/// is natural and varied rather than the same sentence every time. It stays grounded and safe by
/// building on the deterministic <see cref="Companion.Core.Services.Greeter"/>: that supplies the
/// real remembered threads (open loops, recent projects) and the offline fallback message. The
/// model is only asked to phrase the welcome around those real threads — it never invents them — and
/// any failure or empty reply falls straight back to the deterministic opener.
/// </summary>
public sealed class LlmGreeter : IGreeter, IGreetingRephraser
{
    private const int MaxGreetingChars = 1200;

    private static readonly Regex SpecialTokens = new(@"<\|[^|>]{1,32}\|>", RegexOptions.CultureInvariant);

    private const string SystemPrompt =
        "You are a persistent AI companion. At the start of a session you speak first — a brief, " +
        "warm, natural greeting (one to three sentences) so the user never faces a blank prompt. " +
        "Vary your wording between sessions. Only reference threads you are actually given; never " +
        "invent things you remember. Do not output a bulleted list, headings, or your notes — just " +
        "speak to the user directly.";

    private readonly IGreeter _fallback;
    private readonly IChatModel _chat;
    private readonly ILogger<LlmGreeter> _logger;
    // Optional: when both are supplied the opener is written in the user's identity + personality;
    // without them (e.g. a bare unit test) the greeter falls back to a neutral voice.
    private readonly IPersonalityService? _personality;
    private readonly IProfileStore? _profiles;

    public LlmGreeter(
        IGreeter fallback, IChatModel chat, ILogger<LlmGreeter> logger,
        IPersonalityService? personality = null, IProfileStore? profiles = null)
    {
        _fallback = fallback;
        _chat = chat;
        _logger = logger;
        _personality = personality;
        _profiles = profiles;
    }

    public async Task<Greeting> GreetAsync(string userId, CancellationToken ct = default)
    {
        // The deterministic greeter is both the grounding (real threads → openers) and the safety net.
        var grounded = await _fallback.GreetAsync(userId, ct);
        return await RephraseAsync(grounded, userId, ct);
    }

    public async Task<Greeting> RephraseAsync(Greeting grounded, string? userId = null, CancellationToken ct = default)
    {
        try
        {
            var system = await SystemPromptForAsync(userId, ct);
            var reply = await _chat.CompleteAsync(system, BuildPrompt(grounded.Openers), jsonMode: false, ct: ct);
            // Small local models sometimes leak chat-template markers (e.g. a literal <|im_end|>)
            // into their text; they are never legitimate greeting content.
            var message = SpecialTokens.Replace(reply.Text ?? string.Empty, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(message))
                return grounded;

            if (message.Length > MaxGreetingChars)
                message = message[..MaxGreetingChars].TrimEnd();

            // Keep the real openers for the UI's quick-pick chips; only the phrasing is model-written.
            return grounded with { Message = message };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session ended (client disconnect, Ctrl-C) while the model was writing — there is
            // no one left to greet. Not a model failure; let the caller unwind normally.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM greeting failed; using the deterministic opener.");
            return grounded;
        }
    }

    /// <summary>
    /// The greeting system prompt, prefixed with the user's identity + personality when known so the
    /// opener is written in character. Falls back to the neutral prompt when no user is given.
    /// </summary>
    private async Task<string> SystemPromptForAsync(string? userId, CancellationToken ct)
    {
        if (userId is null || _profiles is null || _personality is null)
            return SystemPrompt;

        var profile = await _profiles.GetOrCreateAsync(userId, ct);
        var persona = _personality.Compose(profile);
        return string.IsNullOrWhiteSpace(persona)
            ? SystemPrompt
            : "Stay fully in character as described here:\n" + persona + "\n\n" + SystemPrompt;
    }

    private static string BuildPrompt(IReadOnlyList<string> openers)
        => openers.Count == 0
            ? "You don't remember anything about this user yet — this may be your first conversation. " +
              "Write a warm, brief opening greeting that invites them to talk about anything or to ask " +
              "what you can do. Don't claim to remember anything."
            : "Threads you actually remember with this user (reference one or two of them naturally, " +
              "and make clear they can also just talk about anything else):\n" +
              string.Join("\n", openers.Select(o => "- " + o)) +
              "\n\nWrite your opening greeting now.";
}
