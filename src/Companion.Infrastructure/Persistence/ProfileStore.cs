using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Companion.Infrastructure.Persistence;

/// <summary>EF Core-backed user profile store (identity + editable persona).</summary>
public sealed class ProfileStore : IProfileStore
{
    private readonly CompanionDbContext _db;
    private readonly TimeProvider _clock;

    public ProfileStore(CompanionDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Get-or-create is a read-then-insert, so two callers arriving together for a user who has
    /// no row yet will BOTH try to insert and one will lose on the primary key. That happens for
    /// real on a fresh install — the greeting, the first message, and the background worker can
    /// all land at once — and an unhandled unique violation there would fail the user's very
    /// first turn. Losing the race is not an error: the row now exists, so re-read it.
    /// </summary>
    public async Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (profile is not null)
            return profile;

        profile = new UserProfile { UserId = userId, CreatedAt = _clock.GetUtcNow() };
        _db.Users.Add(profile);
        try
        {
            await _db.SaveChangesAsync(ct);
            return profile;
        }
        catch (DbUpdateException)
        {
            // Someone else created it between our read and our insert. Drop our losing copy so
            // the context isn't left holding a duplicate, then take theirs. A unique violation
            // with no row afterwards is a genuine failure, so that case rethrows.
            _db.Entry(profile).State = EntityState.Detached;
            var winner = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
            if (winner is null)
                throw;
            return winner;
        }
    }

    public async Task SetPersonaAsync(string userId, string? persona, CancellationToken ct = default)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        profile.Persona = persona;
        _db.Users.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetPersonalityPresetAsync(string userId, string? presetName, CancellationToken ct = default)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        profile.PersonalityPreset = presetName;
        _db.Users.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetIdentityAsync(
        string userId, string? name, string? gender, string? pronouns, CancellationToken ct = default)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        // Only overwrite the fields that were actually provided.
        if (name is not null) profile.CompanionName = name;
        if (gender is not null) profile.CompanionGender = gender;
        if (pronouns is not null) profile.CompanionPronouns = pronouns;
        _db.Users.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetCompanionSpiritsAsync(
        string userId, double spirits, DateTimeOffset nudgedAt, CancellationToken ct = default)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        profile.CompanionSpirits = Math.Clamp(spirits, -1.0, 1.0);
        profile.CompanionSpiritsNudgedAt = nudgedAt;
        _db.Users.Update(profile);
        await _db.SaveChangesAsync(ct);
    }
}
