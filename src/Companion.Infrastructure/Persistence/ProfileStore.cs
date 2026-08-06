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

    public async Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId, CreatedAt = _clock.GetUtcNow() };
            _db.Users.Add(profile);
            await _db.SaveChangesAsync(ct);
        }
        return profile;
    }

    public async Task SetPersonaAsync(string userId, string? persona, CancellationToken ct = default)
    {
        var profile = await GetOrCreateAsync(userId, ct);
        profile.Persona = persona;
        _db.Users.Update(profile);
        await _db.SaveChangesAsync(ct);
    }
}
