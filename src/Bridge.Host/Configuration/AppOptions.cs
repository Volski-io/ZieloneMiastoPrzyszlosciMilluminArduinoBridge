using System.IO.Ports;
using Bridge.Core.Bridge;

namespace Bridge.Host.Configuration;

public sealed record AppOptions
{
    public SerialOptions Serial { get; init; } = new();
    public MilluminOptions Millumin { get; init; } = new();
    public ReliabilityOptions Reliability { get; init; } = new();
    public StateOptions State { get; init; } = new();
    public LoggingOptions Logging { get; init; } = new();
    public List<ScenarioOptions> Scenarios { get; init; } =
        Enumerable.Range(1, 8)
            .Select(number => new ScenarioOptions { Scenario = number, Column = new ColumnTarget { Index = number } })
            .ToList();

    public BridgeCoordinatorOptions ToCoordinatorOptions() => new()
    {
        ScenarioColumns = Scenarios.ToDictionary(item => item.Scenario, item => item.Column),
        DuplicateInputWindow = TimeSpan.FromMilliseconds(Reliability.DuplicateInputWindowMs),
        ModelAckTimeout = TimeSpan.FromMilliseconds(Reliability.ModelAckTimeoutMs),
        ModelRetryWindow = TimeSpan.FromMilliseconds(Reliability.ModelRetryWindowMs),
        TickInterval = TimeSpan.FromMilliseconds(Reliability.TickIntervalMs),
        MilluminPingInterval = TimeSpan.FromMilliseconds(Reliability.MilluminPingIntervalMs),
        MilluminOfflineAfter = TimeSpan.FromMilliseconds(Reliability.MilluminOfflineAfterMs),
        StartupReconcileDelay = TimeSpan.FromMilliseconds(Reliability.StartupReconcileDelayMs),
        RestoreLastScenario = Reliability.RestoreLastScenario
    };

    public void Validate()
    {
        if (Serial.BaudRate <= 0 || Serial.DataBits is < 5 or > 8)
        {
            throw new InvalidOperationException("Serial configuration is invalid.");
        }

        if (Serial.StartupDelayMs < 0 || Serial.ReconnectDelayMs < 0)
        {
            throw new InvalidOperationException("Serial delays must not be negative.");
        }

        if (Millumin.InputPort is <= 0 or > 65535 || Millumin.FeedbackPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Millumin OSC ports must be between 1 and 65535.");
        }

        if (Scenarios.Count != 8 || Scenarios.Select(item => item.Scenario).Distinct().Count() != 8 ||
            Scenarios.Any(item => item.Scenario is < 1 or > 8))
        {
            throw new InvalidOperationException("Exactly one mapping is required for each scenario 1-8.");
        }

        foreach (var scenario in Scenarios)
        {
            _ = scenario.Column.ToOscArgument();
        }

        if (Reliability.ModelAckTimeoutMs < 25)
        {
            throw new InvalidOperationException("At 2400 baud, ModelAckTimeoutMs must be at least 25 ms.");
        }

        if (Reliability.ModelRetryWindowMs < Reliability.ModelAckTimeoutMs)
        {
            throw new InvalidOperationException("ModelRetryWindowMs must not be shorter than ModelAckTimeoutMs.");
        }
    }
}

public sealed record SerialOptions
{
    public string? PortName { get; init; }
    public int BaudRate { get; init; } = 2400;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake Handshake { get; init; } = Handshake.None;
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }
    public bool RequireUniqueMatch { get; init; } = true;
    public int StartupDelayMs { get; init; }
    public int ReconnectDelayMs { get; init; } = 2000;
    public List<string> PortPatterns { get; init; } =
    [
        "/dev/cu.usbmodem*",
        "/dev/cu.usbserial*",
        "/dev/cu.SLAB_USBtoUART*",
        "/dev/cu.wchusbserial*"
    ];
}

public sealed record MilluminOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int InputPort { get; init; } = 5000;
    public int FeedbackPort { get; init; } = 5001;
}

public sealed record ReliabilityOptions
{
    public int DuplicateInputWindowMs { get; init; } = 250;
    public int ModelAckTimeoutMs { get; init; } = 45;
    public int ModelRetryWindowMs { get; init; } = 100;
    public int TickIntervalMs { get; init; } = 10;
    public int MilluminPingIntervalMs { get; init; } = 2000;
    public int MilluminOfflineAfterMs { get; init; } = 6000;
    public int StartupReconcileDelayMs { get; init; } = 2000;
    public bool RestoreLastScenario { get; init; } = true;
}

public sealed record StateOptions
{
    public string FilePath { get; init; } = "~/.local/share/MilluminArduinoBridge/state.json";
}

public sealed record LoggingOptions
{
    public bool Debug { get; init; }
}

public sealed record ScenarioOptions
{
    public int Scenario { get; init; }
    public ColumnTarget Column { get; init; } = new();
}
