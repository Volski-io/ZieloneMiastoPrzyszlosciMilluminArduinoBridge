namespace Bridge.Core.Bridge;

public sealed record BridgeState
{
    public int? DesiredScenario { get; init; }
    public int? ObservedScenario { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
