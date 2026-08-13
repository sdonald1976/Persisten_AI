using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly CompanionDbContext _db;
    private readonly ModelOptions _models;

    public CapabilityRegistry(CompanionDbContext db, ModelOptions models)
    {
        _db = db;
        _models = models;
    }

    public async Task<IReadOnlyList<CapabilityDescriptor>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        return await _db.Capabilities.OrderBy(c => c.Id).ToListAsync(ct);
    }

    public async Task<string> RenderSummaryAsync(string? query = null, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        var relevant = string.IsNullOrWhiteSpace(query)
            ? all
            : all.Where(c => c.Availability == CapabilityAvailability.Available
                || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var available = relevant.Where(c => c.Availability == CapabilityAvailability.Available).Take(6).ToList();
        var uncertain = relevant.Where(c => c.Availability == CapabilityAvailability.Untested).Take(3).ToList();
        var unavailable = relevant.Where(c => c.Availability is CapabilityAvailability.Unavailable or CapabilityAvailability.Degraded).Take(8).ToList();

        var lines = new List<string>();
        if (available.Count > 0)
        {
            lines.Add("You can:");
            lines.AddRange(available.Select(c => $"- {c.Description}"));
        }
        if (uncertain.Count > 0)
        {
            lines.Add("Available but not verified here:");
            lines.AddRange(uncertain.Select(c => $"- {c.Description}"));
        }
        if (unavailable.Count > 0)
        {
            lines.Add("You cannot currently:");
            lines.AddRange(unavailable.Select(c => $"- {c.Description}"));
        }
        return string.Join("\n", lines);
    }

    public async Task MarkSuccessAsync(string id, DateTimeOffset now, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var cap = await _db.Capabilities.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cap is null)
            return;
        cap.Availability = CapabilityAvailability.Available;
        cap.Confidence = Math.Min(1, Math.Max(cap.Confidence, 0.75));
        cap.LastVerifiedAt = now;
        cap.ConsecutiveFailures = 0;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkFailureAsync(string id, DateTimeOffset now, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var cap = await _db.Capabilities.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cap is null)
            return;
        cap.ConsecutiveFailures++;
        cap.Availability = cap.ConsecutiveFailures >= 3 ? CapabilityAvailability.Unavailable : CapabilityAvailability.Degraded;
        cap.Confidence = Math.Max(0, cap.Confidence - 0.25);
        cap.LastVerifiedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Applies a provider model probe. A missing model is the one failure we can state with
    /// certainty before any call is made, so it goes straight to Unavailable rather than through
    /// the three-strikes degradation used for live call failures.
    ///
    /// A present model only restores the row to its seeded expectation — it proves the model is
    /// installed, not that a call succeeds — and never overwrites live failure history, which is
    /// stronger evidence than a catalog listing.
    /// </summary>
    public async Task ApplyModelProbeAsync(
        IReadOnlyDictionary<string, bool> presenceByModel, DateTimeOffset now, CancellationToken ct = default)
    {
        if (presenceByModel.Count == 0)
            return;

        await EnsureSeededAsync(ct);
        var defaults = DefaultCapabilities().ToDictionary(c => c.Id);
        var rows = await _db.Capabilities.ToListAsync(ct);
        var dirty = false;

        foreach (var row in rows)
        {
            if (row.Model is null || !presenceByModel.TryGetValue(row.Model, out var present))
                continue; // not a probed model — leave whatever we already knew

            if (!present)
            {
                row.Availability = CapabilityAvailability.Unavailable;
                row.Confidence = 0;
                row.LastVerifiedAt = now;
                dirty = true;
                continue;
            }

            if (row.ConsecutiveFailures > 0)
                continue; // a real call failed here; a catalog entry doesn't overturn that

            if (row.Availability == CapabilityAvailability.Unavailable
                && defaults.TryGetValue(row.Id, out var def))
            {
                row.Availability = def.Availability;
                row.Confidence = def.Confidence;
            }

            row.LastVerifiedAt = now;
            dirty = true;
        }

        if (dirty)
            await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Keeps the stored rows in step with the CURRENT model configuration, not just seeded once:
    /// missing capabilities are added, obsolete ones removed, and a row whose provider/model no
    /// longer matches the config is reset to the config's defaults — its old verification history
    /// described a different backend and would be a lie about this one. Rows whose backing config
    /// is unchanged are left alone, preserving MarkSuccess/MarkFailure history.
    /// </summary>
    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        var defaults = DefaultCapabilities().ToDictionary(c => c.Id);
        var stored = await _db.Capabilities.ToListAsync(ct);
        var dirty = false;

        foreach (var row in stored)
        {
            if (!defaults.TryGetValue(row.Id, out var def))
            {
                _db.Capabilities.Remove(row); // no longer a capability this build knows
                dirty = true;
                continue;
            }

            if (row.Provider != def.Provider || row.Model != def.Model)
            {
                // The backend changed under this row (e.g. Mock → Ollama, or a new model name).
                // Start over from the config's defaults; verification will rebuild trust.
                row.Provider = def.Provider;
                row.Model = def.Model;
                row.Availability = def.Availability;
                row.Confidence = def.Confidence;
                row.LastVerifiedAt = null;
                row.ConsecutiveFailures = 0;
                dirty = true;
            }
        }

        var known = stored.Select(r => r.Id).ToHashSet();
        var missing = defaults.Values.Where(d => !known.Contains(d.Id)).ToList();
        if (missing.Count > 0)
        {
            _db.Capabilities.AddRange(missing);
            dirty = true;
        }

        if (dirty)
            await _db.SaveChangesAsync(ct);
    }

    private IEnumerable<CapabilityDescriptor> DefaultCapabilities()
    {
        var provider = _models.UsesRealModel ? _models.Provider : "Mock";
        yield return Cap("text.chat", "Conversation", "converse with the user", "text", "text", provider, _models.Chat.Model, CapabilityAvailability.Available, 0.9);
        yield return Cap("memory.search", "Memory search", "remember prior conversations and search relevant memory", "text", "memory context", "deterministic+embedding", _models.Embeddings.Model, CapabilityAvailability.Available, 0.9);
        yield return Cap("task.audit", "Task completion audit", "check whether long responses appear complete", "text", "judgment", provider, _models.TaskAuditorOrSummarizer.Model, CapabilityAvailability.Available, 0.8);
        yield return Cap("privacy.classify", "Privacy classifier", "detect turns that should skip durable memory", "text", "privacy decision", provider, _models.SafetyOrExtraction.Model, CapabilityAvailability.Available, 0.8);
        yield return Cap("image.describe", "Image analysis", "analyze images", "image", "text", provider, _models.Vision?.Model, _models.Vision is null ? CapabilityAvailability.Unavailable : CapabilityAvailability.Untested, _models.Vision is null ? 0 : 0.4);
        yield return Cap("speech.transcribe", "Speech transcription", "transcribe audio", "audio", "text", provider, _models.Transcription?.Model, _models.Transcription is null ? CapabilityAvailability.Unavailable : CapabilityAvailability.Untested, _models.Transcription is null ? 0 : 0.4);
        yield return Cap("speech.synthesize", "Speech synthesis", "speak text aloud", "text", "audio", provider, _models.Speech?.Model, _models.Speech is null ? CapabilityAvailability.Unavailable : CapabilityAvailability.Untested, _models.Speech is null ? 0 : 0.4);
        yield return Cap("web.browse", "Web browsing", "browse arbitrary websites", "url", "web content", "none", null, CapabilityAvailability.Unavailable, 0);
        yield return Cap("local.files", "Local file manipulation", "manipulate local files autonomously", "file", "file", "none", null, CapabilityAvailability.Unavailable, 0);
        yield return Cap("object_detection", "Object detection", "identify objects in images", "image", "labels", provider, _models.Vision?.Model, _models.Vision is null ? CapabilityAvailability.Unavailable : CapabilityAvailability.Untested, _models.Vision is null ? 0 : 0.4);
    }

    private static CapabilityDescriptor Cap(
        string id, string name, string description, string inputs, string outputs, string provider,
        string? model, CapabilityAvailability availability, double confidence)
        => new()
        {
            Id = id, Name = name, Description = description, InputTypes = inputs, OutputTypes = outputs,
            Provider = provider, Model = model, Availability = availability, LocalOnly = true,
            Confidence = confidence,
        };
}
