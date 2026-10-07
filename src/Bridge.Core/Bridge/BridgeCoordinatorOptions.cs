namespace Bridge.Core.Bridge;

public sealed record BridgeCoordinatorOptions
{
    public IReadOnlyDictionary<int, ColumnTarget> ScenarioColumns { get; init; } =
        Enumerable.Range(1, 8).ToDictionary(number => number, number => new ColumnTarget { Index = number });

    public TimeSpan DuplicateInputWindow { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ModelAckTimeout { get; init; } = TimeSpan.FromMilliseconds(45);
    public TimeSpan ModelRetryWindow { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan MilluminPingInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MilluminOfflineAfter { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan StartupReconcileDelay { get; init; } = TimeSpan.FromSeconds(2);
    public bool RestoreLastScenario { get; init; } = true;
}
