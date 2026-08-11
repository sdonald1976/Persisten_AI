using System.Text.RegularExpressions;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class ProcedureStore : IProcedureStore
{
    private static readonly Regex TeachRx = new(
        @"\b(remember this process|learn this workflow|this is how i|this is our .{0,40}process|whenever i ask for)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RevisionRx = new(
        @"\b(?:don't|do not|no longer)\s+do\s+step\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CompanionDbContext _db;
    public ProcedureStore(CompanionDbContext db) => _db = db;

    public async Task<Procedure?> AddOrUpdateFromTeachingAsync(
        string userId, Guid conversationId, Message message, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!TeachRx.IsMatch(message.Content))
            return null;

        var name = NameFrom(message.Content);
        var existing = await _db.Procedures
            .Include(p => p.Steps)
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Status == ProcedureStatus.Active && p.Name == name, ct);
        var steps = ExtractSteps(message.Content).ToList();
        if (steps.Count == 0)
            steps.Add(Clean(message.Content));

        var procedure = existing ?? new Procedure
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Owner = ProcedureOwner.UserTaught,
            Access = ProcedureAccess.Explainable,
            Status = ProcedureStatus.Active,
            Confidence = 0.85,
            SourceConversationId = conversationId,
            SourceMessageId = message.Id,
            Evidence = message.Content,
            CreatedAt = now,
        };

        procedure.Description = $"User-taught procedure: {name}.";
        procedure.UpdatedAt = now;
        if (existing is null)
            _db.Procedures.Add(procedure);
        else
            procedure.Steps.Clear();

        var order = 1;
        foreach (var step in steps)
            procedure.Steps.Add(new ProcedureStep
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ProcedureId = procedure.Id,
                Order = order++,
                Instruction = step,
            });

        procedure.Revisions.Add(new ProcedureRevision
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProcedureId = procedure.Id,
            Timestamp = now,
            Kind = existing is null ? "Created" : "Updated",
            Note = "User explicitly taught this procedure.",
            SourceMessageId = message.Id,
        });

        await _db.SaveChangesAsync(ct);
        return procedure;
    }

    public async Task<Procedure?> ApplyRevisionAsync(
        string userId, Message message, DateTimeOffset now, CancellationToken ct = default)
    {
        var match = RevisionRx.Match(message.Content);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var stepNumber))
            return null;

        var procedure = await SearchAsync(userId, message.Content, 1, ct);
        var found = procedure.FirstOrDefault();
        if (found is null)
            return null;

        var step = await _db.ProcedureSteps.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ProcedureId == found.Id && s.Order == stepNumber, ct);
        if (step is null)
            return null;
        var updated = await _db.ProcedureSteps
            .Where(s => s.UserId == userId && s.ProcedureId == found.Id && s.Order == stepNumber)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsActive, false)
                .SetProperty(x => x.Notes, "User said this step is no longer part of the procedure."), ct);
        if (updated == 0)
            return null;
        await _db.Procedures
            .Where(p => p.UserId == userId && p.Id == found.Id)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.UpdatedAt, now), ct);
        _db.ProcedureRevisions.Add(new ProcedureRevision
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ProcedureId = found.Id,
            Timestamp = now,
            Kind = "StepRemoved",
            Note = $"Step {stepNumber} deactivated: {step.Instruction}",
            SourceMessageId = message.Id,
        });
        await _db.SaveChangesAsync(ct);
        return (await SearchAsync(userId, found.Name, 1, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Procedure>> SearchAsync(
        string userId, string query, int limit, CancellationToken ct = default)
    {
        var all = await _db.Procedures
            .AsNoTracking()
            .Include(p => p.Steps)
            .Where(p => p.UserId == userId && p.Status == ProcedureStatus.Active)
            .ToListAsync(ct);
        return all
            .Select(p => (Procedure: p, Score: ScoreMath.KeywordOverlap(query, p.Name + " " + p.Description + " " + string.Join(" ", p.Steps.Select(s => s.Instruction)))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Procedure)
            .ToList();
    }

    public async Task<IReadOnlyList<ProcedureRevision>> GetRevisionsAsync(string userId, Guid procedureId, CancellationToken ct = default)
        => await _db.ProcedureRevisions
            .Where(r => r.UserId == userId && r.ProcedureId == procedureId)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(ct);

    private static string NameFrom(string text)
    {
        var lower = text.ToLowerInvariant();
        var markers = new[] { "this is how i", "this is our", "whenever i ask for", "learn this workflow", "remember this process" };
        foreach (var marker in markers)
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = text[(idx + marker.Length)..];
                var stop = FirstBoundary(rest);
                if (stop > 0)
                    rest = rest[..stop];
                rest = rest.Trim(" .:-".ToCharArray());
                var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray();
                if (words.Length > 0)
                    return CultureTitle(string.Join(' ', words));
            }
        }
        return "User Workflow";
    }

    private static IEnumerable<string> ExtractSteps(string text)
    {
        var stepText = text;
        var colon = stepText.IndexOf(':');
        if (colon >= 0 && colon < stepText.Length - 1)
            stepText = stepText[(colon + 1)..];

        var pieces = Regex.Split(
                stepText,
                @"(?:^|\s|;\s*)(?:\d+[\).\:]|first[,]?|second[,]?|third[,]?|fourth[,]?|fifth[,]?|then|finally)\s+",
                RegexOptions.IgnoreCase)
            .SelectMany(p => p.Split(';'))
            .Select(Clean)
            .Where(s => s.Length >= 4 && !TeachRx.IsMatch(s))
            .Take(12)
            .ToList();

        return pieces.Count > 1 ? pieces : pieces.Where(s => s.Length >= 8);
    }

    private static string Clean(string text) => Regex.Replace(text.Trim(), @"\s+", " ").Trim(' ', '.', ':', ';');

    private static int FirstBoundary(string text)
    {
        var boundaries = new[]
        {
            text.IndexOf(':'),
            Regex.Match(text, @"\b1[\).\:]", RegexOptions.IgnoreCase).Index,
            Regex.Match(text, @"\bfirst\b", RegexOptions.IgnoreCase).Index,
            Regex.Match(text, @"\bthen\b", RegexOptions.IgnoreCase).Index,
        }.Where(i => i > 0).DefaultIfEmpty(-1);
        return boundaries.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
    }

    private static string CultureTitle(string text)
        => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
}
