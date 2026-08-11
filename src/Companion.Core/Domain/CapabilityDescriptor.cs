namespace Companion.Core.Domain;

public sealed class CapabilityDescriptor
{
    public string Id { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string InputTypes { get; set; } = default!;
    public string OutputTypes { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string? Model { get; set; }
    public CapabilityAvailability Availability { get; set; } = CapabilityAvailability.Untested;
    public bool LocalOnly { get; set; } = true;
    public double Confidence { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
}
