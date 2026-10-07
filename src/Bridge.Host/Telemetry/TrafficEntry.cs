namespace Bridge.Host.Telemetry;

public sealed record TrafficEntry(
    long Id,
    DateTimeOffset Timestamp,
    string Channel,
    string Direction,
    string Summary,
    string Translation,
    string RawBytes);
