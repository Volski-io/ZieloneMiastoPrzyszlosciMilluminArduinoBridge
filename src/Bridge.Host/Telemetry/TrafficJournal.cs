using Bridge.Core.ModelProtocol;
using Bridge.Core.Osc;

namespace Bridge.Host.Telemetry;

public sealed class TrafficJournal
{
    private const int Capacity = 2000;
    private readonly object _gate = new();
    private readonly Queue<TrafficEntry> _entries = [];
    private readonly ModelFrameParser _receiveParser = new();
    private long _nextId;

    public TrafficJournal()
    {
        _receiveParser.FrameParsed += RecordReceivedFrame;
    }

    private bool _serialConnected;
    private string? _serialPort;
    private DateTimeOffset? _lastOscReceivedAt;

    public bool SerialConnected { get { lock (_gate) { return _serialConnected; } } }
    public string? SerialPort { get { lock (_gate) { return _serialPort; } } }
    public DateTimeOffset? LastOscReceivedAt { get { lock (_gate) { return _lastOscReceivedAt; } } }

    public IReadOnlyList<TrafficEntry> GetAfter(long afterId)
    {
        lock (_gate)
        {
            return _entries.Where(item => item.Id > afterId).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public void RecordSerialReceived(ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            _receiveParser.Feed(bytes);
        }
    }

    public void RecordSerialSent(ReadOnlySpan<byte> bytes)
    {
        var (summary, translation) = DescribeFrame(bytes);
        Add("UART", "TX", summary, translation, FormatBytes(bytes));
    }

    public void RecordSerialConnection(bool connected, string? port)
    {
        lock (_gate)
        {
            _serialConnected = connected;
            _serialPort = connected ? port : null;
            if (!connected)
            {
                _receiveParser.Reset();
            }
        }

        Add("SYSTEM", connected ? "UP" : "DOWN",
            connected ? $"Połączono port {port}" : "Port szeregowy rozłączony",
            connected ? "UART gotowy do transmisji." : "Trwa automatyczna próba ponownego połączenia.",
            string.Empty);
    }

    public void RecordOsc(string direction, OscMessage message, ReadOnlySpan<byte> packet)
    {
        // Millumin heartbeat is transport noise, not a user command. Keep it out of
        // the panel so the useful RX/TX history remains readable during long tests.
        if (direction.Equals("TX", StringComparison.OrdinalIgnoreCase)
            && message.Address.Equals("/ping", StringComparison.Ordinal))
        {
            return;
        }

        if (direction.Equals("RX", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _lastOscReceivedAt = DateTimeOffset.UtcNow;
            }
        }

        Add(
            "OSC",
            direction,
            message.Address,
            message.Arguments.Count == 0 ? "Brak argumentów" : string.Join(", ", message.Arguments.Select(FormatArgument)),
            FormatBytes(packet));
    }

    public void RecordSystem(string summary, string translation) =>
        Add("SYSTEM", "INFO", summary, translation, string.Empty);

    private void RecordReceivedFrame(ModelFrame frame)
    {
        var bytes = new[] { ModelProtocolConstants.StartByte, frame.Data, frame.ReceivedChecksum };
        var command = ModelCommandCatalog.Get(frame.Data);
        var validity = frame.IsValid
            ? "CRC poprawne"
            : $"CRC błędne, oczekiwano 0x{frame.ExpectedChecksum:X2}";
        Add("UART", "RX", command.DataHex, $"{command.Name}; {validity}", FormatBytes(bytes));
    }

    private static (string Summary, string Translation) DescribeFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == ModelFrameCodec.FrameLength && bytes[0] == ModelProtocolConstants.StartByte)
        {
            var command = ModelCommandCatalog.Get(bytes[1]);
            var expected = Crc8.Compute(bytes[1]);
            var validity = expected == bytes[2] ? "CRC poprawne" : $"CRC błędne, oczekiwano 0x{expected:X2}";
            return (command.DataHex, $"{command.Name}; {validity}");
        }

        return ("Dane surowe", $"{bytes.Length} bajtów");
    }

    private void Add(string channel, string direction, string summary, string translation, string rawBytes)
    {
        lock (_gate)
        {
            _entries.Enqueue(new TrafficEntry(
                ++_nextId,
                DateTimeOffset.Now,
                channel,
                direction,
                summary,
                translation,
                rawBytes));

            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', bytes.ToArray().Select(value => value.ToString("X2")));

    private static string FormatArgument(object argument) => argument switch
    {
        string text => $"\"{text}\"",
        bool boolean => boolean ? "true" : "false",
        _ => argument.ToString() ?? string.Empty
    };
}
