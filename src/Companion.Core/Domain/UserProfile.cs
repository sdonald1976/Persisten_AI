namespace Companion.Core.Domain;

/// <summary>
/// Identity and isolation root. Every other record is scoped by <see cref="UserId"/>,
/// which is how the system enforces that one user's data never leaks into another's.
/// </summary>
public class UserProfile
{
    public string UserId { get; set; } = default!;
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
