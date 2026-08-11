using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Text;
using Microsoft.Extensions.Options;

namespace Companion.Core.Services;

public sealed class AttentionService : IAttentionService
{
    private readonly IAttentionStore _store;
    private readonly CompanionOptions _options;
    private readonly TimeProvider _clock;

    public AttentionService(IAttentionStore store, IOptions<CompanionOptions> options, TimeProvider clock)
    {
        _store = store;
        _options = options.Value;
        _clock = clock;
    }

    public async Task CaptureTurnAsync(string userId, Message message, bool remember, CancellationToken ct = default)
    {
        if (!remember || message.Role != MessageRole.User)
            return;

        var now = _clock.GetUtcNow();
        await _store.ExpireOldAsync(userId, now, ct);
        if (IsResolution(message.Content))
        {
            foreach (var item in await _store.GetActiveAsync(userId, _options.MaxAttentionItems, ct))
            {
                if (ScoreMath.KeywordOverlap(message.Content, item.Subject + " " + item.Summary) > 0.05)
                {
                    item.Status = AttentionStatus.Resolved;
                    await _store.UpdateAsync(item, ct);
                }
            }
            return;
        }

        if (!LooksImportant(message.Content))
            return;

        var subject = SubjectFrom(message.Content);
        await _store.UpsertAsync(new AttentionItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = subject,
            Summary = Summarize(message.Content, subject),
            SourceType = AttentionSourceType.Conversation,
            SourceId = message.Id.ToString(),
            Owner = MemoryOwner.Shared,
            Strength = 0.35,
            CreatedAt = now,
            LastActivatedAt = now,
            ExpiresAt = now.AddDays(_options.AttentionTtlDays),
            Status = AttentionStatus.Active,
        }, ct);
    }

    public async Task<IReadOnlyList<string>> SelectForContextAsync(
        string userId, string query, int limit, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        await _store.ExpireOldAsync(userId, now, ct);
        var active = await _store.GetActiveAsync(userId, Math.Min(limit, _options.MaxAttentionItems), ct);
        return active
            .Select(a => (Item: a, Score: a.Strength + ScoreMath.KeywordOverlap(query, a.Subject + " " + a.Summary)))
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Item.Summary)
            .ToList();
    }

    private static bool LooksImportant(string text)
        => Regex.IsMatch(text, @"\b(test|testing|debug|identity|procedure|workflow|remember this|important|blocked|stuck|changed|deploy|review)\b",
            RegexOptions.IgnoreCase);

    private static bool IsResolution(string text)
        => Regex.IsMatch(text, @"\b(done|completed|resolved|fixed|finished|closed out)\b", RegexOptions.IgnoreCase);

    private static string SubjectFrom(string text)
    {
        if (Regex.IsMatch(text, @"\bidentity\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(text, @"\bharden", RegexOptions.IgnoreCase))
            return "identity hardening";
        if (Regex.IsMatch(text, @"\bworkflow|procedure\b", RegexOptions.IgnoreCase))
            return "workflow procedure";
        if (Regex.IsMatch(text, @"\bdeploy", RegexOptions.IgnoreCase))
            return "deployment";
        if (Regex.IsMatch(text, @"\bdebug", RegexOptions.IgnoreCase))
            return "debugging";
        return string.Join(' ', Tokenizer.Tokenize(text).Where(t => t.Length > 3).Take(5)).Trim();
    }

    private static string Summarize(string text, string subject)
    {
        var cleaned = Regex.Replace(text.Trim(), @"\s+", " ");
        if (cleaned.Length > 180)
            cleaned = cleaned[..180].TrimEnd() + "...";
        return cleaned.Length == 0 ? $"The current topic is {subject}." : cleaned;
    }
}
