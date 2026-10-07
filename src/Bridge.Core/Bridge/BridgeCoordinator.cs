using System.Threading.Channels;
using System.Globalization;
using Bridge.Core.Abstractions;
using Bridge.Core.ModelProtocol;
using Bridge.Core.Osc;

namespace Bridge.Core.Bridge;

public sealed class BridgeCoordinator
{
    private readonly ISerialTransport _serial;
    private readonly IOscTransport _osc;
    private readonly IStateStore _stateStore;
    private readonly IBridgeLog _log;
    private readonly BridgeCoordinatorOptions _options;
    private readonly ModelFrameParser _parser = new();
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<byte, DateTimeOffset> _lastInputAt = [];
    private readonly Queue<byte> _modelOutputQueue = [];
    private static readonly IReadOnlyDictionary<string, SectorOscTarget> SectorOscTargets =
        new Dictionary<string, SectorOscTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["/makieta/slupy"] = new(6, 0x21),
            ["/makieta/balony/led"] = new(3, 0x23),
            ["/makieta/zabudowa-mieszkaniowa/0"] = new(3, 0x5F),
            ["/makieta/budynek/1"] = new(6, 0x61),
            ["/makieta/budynek/2"] = new(7, 0x63),
            ["/makieta/budynek/3"] = new(8, 0x65),
            ["/makieta/hotel/1"] = new(3, 0x67),
            ["/makieta/zabudowa-mieszkaniowa/1"] = new(3, 0x6B),
            ["/makieta/farma-fotowoltaiczna/1/rgb"] = new(3, 0x6F),
            ["/makieta/magazyn-energii/rgb"] = new(2, 0x73)
        };

    private BridgeState _state = new();
    private PendingModelCommand? _pendingModelCommand;
    private DateTimeOffset _nextPingAt;
    private DateTimeOffset? _lastMilluminMessageAt;
    private DateTimeOffset? _reconcileAt;
    private bool _milluminOnline;
    private long _milluminConnectionEpoch;
    private long _reconciledEpoch = -1;

    public BridgeCoordinator(
        ISerialTransport serial,
        IOscTransport osc,
        IStateStore stateStore,
        IBridgeLog log,
        BridgeCoordinatorOptions options)
    {
        _serial = serial;
        _osc = osc;
        _stateStore = stateStore;
        _log = log;
        _options = options;

        _serial.BytesReceived += OnSerialBytesReceived;
        _serial.ConnectionChanged += OnSerialConnectionChanged;
        _osc.MessageReceived += OnOscMessageReceived;
        _parser.FrameParsed += OnFrameParsed;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var persistedState = await _stateStore.LoadAsync(cancellationToken);
        // Observed state is never trusted across a process or Millumin restart.
        // A fresh OSC feedback message may repopulate it before reconciliation.
        _state = persistedState with { ObservedScenario = null };
        _nextPingAt = DateTimeOffset.UtcNow;
        _log.Info($"State loaded. Desired scenario: {_state.DesiredScenario?.ToString() ?? "none"}.");

        var timerTask = ProduceTicksAsync(cancellationToken);

        try
        {
            await foreach (var bridgeEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                switch (bridgeEvent)
                {
                    case SerialBytesEvent serialBytes:
                        _parser.Feed(serialBytes.Bytes.Span);
                        break;
                    case ParsedFrameEvent parsedFrame:
                        await HandleFrameAsync(parsedFrame.Frame, cancellationToken);
                        break;
                    case OscMessageEvent oscMessage:
                        await HandleOscMessageAsync(oscMessage.Message, cancellationToken);
                        break;
                    case SerialConnectionEvent serialConnection:
                        HandleSerialConnection(serialConnection);
                        break;
                    case TickEvent tick:
                        await HandleTickAsync(tick.Now, cancellationToken);
                        break;
                    case ManualModelCommandEvent command:
                        await HandleManualModelCommandAsync(command.Data, command.Source, cancellationToken);
                        break;
                }
            }
        }
        finally
        {
            await timerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public bool TryQueueModelCommand(byte data, string source = "panel WWW")
    {
        if (data == ModelProtocolConstants.StartByte)
        {
            return false;
        }

        return _events.Writer.TryWrite(new ManualModelCommandEvent(data, source));
    }

    public bool TryQueueOscModelCommand(string address)
    {
        var message = new OscMessage(address);
        if (!TryReadModelCommands(message, out var commands) || commands.Count == 0)
        {
            return false;
        }

        return _events.Writer.TryWrite(new OscMessageEvent(message));
    }

    private void OnSerialBytesReceived(ReadOnlyMemory<byte> bytes) =>
        _events.Writer.TryWrite(new SerialBytesEvent(bytes.ToArray()));

    private void OnSerialConnectionChanged(bool connected, string? port) =>
        _events.Writer.TryWrite(new SerialConnectionEvent(connected, port));

    private void OnOscMessageReceived(OscMessage message) =>
        _events.Writer.TryWrite(new OscMessageEvent(message));

    private void OnFrameParsed(ModelFrame frame) =>
        _events.Writer.TryWrite(new ParsedFrameEvent(frame));

    private async Task ProduceTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _events.Writer.TryWrite(new TickEvent(DateTimeOffset.UtcNow));
        }
    }

    private async Task HandleFrameAsync(ModelFrame frame, CancellationToken cancellationToken)
    {
        if (!frame.IsValid)
        {
            _log.Warning($"Invalid CRC for 0x{frame.Data:X2}: received 0x{frame.ReceivedChecksum:X2}, expected 0x{frame.ExpectedChecksum:X2}.");
            await SendRawModelFrameAsync(ModelProtocolConstants.ChecksumFailed, cancellationToken);
            return;
        }

        _log.Debug($"Model RX 0x{frame.Data:X2}.");

        if (frame.Data is ModelProtocolConstants.AckFailed or ModelProtocolConstants.ChecksumFailed)
        {
            await RetryPendingModelCommandAsync(DateTimeOffset.UtcNow, cancellationToken, immediate: true);
            return;
        }

        if (ModelProtocolConstants.TryGetAcknowledgedData(frame.Data, out var acknowledgedData))
        {
            await HandleModelAcknowledgementAsync(frame.Data, acknowledgedData, cancellationToken);
            return;
        }

        if (ModelProtocolConstants.TryCreateAcknowledgement(frame.Data, out var acknowledgement))
        {
            await SendRawModelFrameAsync(acknowledgement, cancellationToken);
        }

        if (ModelProtocolConstants.TryGetScenario(frame.Data, out var scenario))
        {
            await HandleScenarioButtonAsync(frame.Data, scenario, cancellationToken);
        }
    }

    private async Task HandleScenarioButtonAsync(byte data, int scenario, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastInputAt.TryGetValue(data, out var previous) && now - previous < _options.DuplicateInputWindow)
        {
            _log.Debug($"Duplicate scenario {scenario} input ignored after ACK.");
            return;
        }

        _lastInputAt[data] = now;
        if (!_options.ScenarioColumns.TryGetValue(scenario, out var target))
        {
            _log.Warning($"Scenario {scenario} has no Millumin column mapping.");
            return;
        }

        _state = _state with
        {
            DesiredScenario = scenario,
            UpdatedAtUtc = now
        };
        await _stateStore.SaveAsync(_state, cancellationToken);
        await LaunchTargetAsync(scenario, target, cancellationToken);
    }

    private async Task LaunchTargetAsync(int scenario, ColumnTarget target, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(target.Board))
        {
            await _osc.SendAsync(new OscMessage("/action/selectBoard", target.Board), cancellationToken);
        }

        await _osc.SendAsync(new OscMessage("/action/launchColumn", target.ToOscArgument()), cancellationToken);
        _log.Info($"Scenario {scenario}: launch request sent to Millumin ({target.Name ?? target.Index?.ToString()}).");
    }

    private async Task HandleOscMessageAsync(OscMessage message, CancellationToken cancellationToken)
    {
        if (TryReadModelCommands(message, out var modelCommands))
        {
            if (modelCommands.Count == 0)
            {
                _log.Warning($"Nieprawidłowa lub nieobsługiwana komenda OSC makiety: {message.Address}.");
                return;
            }

            _log.Info(
                $"OSC {message.Address}: sekwencja {string.Join(" → ", modelCommands.Select(value => $"0x{value:X2}"))}.");
            foreach (var modelCommand in modelCommands)
            {
                await HandleManualModelCommandAsync(modelCommand, $"OSC {message.Address}", cancellationToken);
            }
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _lastMilluminMessageAt = now;
        if (!_milluminOnline)
        {
            _milluminOnline = true;
            _milluminConnectionEpoch++;
            _reconcileAt = now + _options.StartupReconcileDelay;
            _log.Info("Millumin OSC feedback detected.");
        }

        if (message.Address.Equals("/millumin/board/launchedColumn", StringComparison.OrdinalIgnoreCase))
        {
            var (index, name) = ReadColumnIdentity(message);
            var scenario = FindScenario(index, name);
            _state = _state with
            {
                DesiredScenario = scenario ?? _state.DesiredScenario,
                ObservedScenario = scenario,
                UpdatedAtUtc = now
            };
            await _stateStore.SaveAsync(_state, cancellationToken);
            QueueModelStatus(ModelProtocolConstants.ScenarioRunning);
            _log.Info($"Millumin launched column {index?.ToString() ?? "?"} '{name ?? string.Empty}', mapped scenario: {scenario?.ToString() ?? "none"}.");
        }
        else if (message.Address.Equals("/millumin/board/stoppedColumn", StringComparison.OrdinalIgnoreCase))
        {
            var (index, name) = ReadColumnIdentity(message);
            var stoppedScenario = FindScenario(index, name);
            if (stoppedScenario.HasValue && stoppedScenario == _state.ObservedScenario)
            {
                _state = _state with
                {
                    DesiredScenario = _state.DesiredScenario == stoppedScenario
                        ? null
                        : _state.DesiredScenario,
                    ObservedScenario = null,
                    UpdatedAtUtc = now
                };
                await _stateStore.SaveAsync(_state, cancellationToken);
                QueueModelStatus(ModelProtocolConstants.NoScenario);
            }
        }
    }

    private async Task HandleManualModelCommandAsync(byte data, string source, CancellationToken cancellationToken)
    {
        if (data == ModelProtocolConstants.StartByte)
        {
            _log.Warning($"{source}: 0x29 is the frame marker and cannot be sent as DATA.");
            return;
        }

        _log.Info($"{source}: queued model command 0x{data:X2} ({ModelCommandCatalog.Get(data).Name}).");
        if (ModelProtocolConstants.TryCreateAcknowledgement(data, out _))
        {
            _modelOutputQueue.Enqueue(data);
            return;
        }

        await SendRawModelFrameAsync(data, cancellationToken);
    }

    private static bool TryReadModelCommands(OscMessage message, out IReadOnlyList<byte> commands)
    {
        if (ModelCommandCatalog.TryGetByOscAddress(message.Address, out var definition))
        {
            commands = [definition.Data];
            return true;
        }

        var segments = message.Address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (TryReadNumberedDeviceSequence(segments, out commands))
        {
            return true;
        }

        if (TryReadSectorSequence(segments, out commands))
        {
            return true;
        }

        const string prefix = "/bridge/model/command/";
        if (message.Address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var value = message.Address[prefix.Length..];
            if (byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var legacyData))
            {
                commands = [legacyData];
                return true;
            }

            commands = [];
            return true;
        }

        if (!message.Address.Equals("/bridge/model/command", StringComparison.OrdinalIgnoreCase) ||
            message.Arguments.Count == 0)
        {
            commands = [];
            return message.Address.StartsWith("/makieta/", StringComparison.OrdinalIgnoreCase);
        }

        switch (message.Arguments[0])
        {
            case int integer when integer is >= byte.MinValue and <= byte.MaxValue:
                commands = [(byte)integer];
                return true;
            case string text:
                text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
                if (byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var textData))
                {
                    commands = [textData];
                    return true;
                }

                commands = [];
                return true;
            default:
                commands = [];
                return true;
        }
    }

    private static bool TryReadSectorSequence(string[] segments, out IReadOnlyList<byte> commands)
    {
        commands = [];
        if (segments.Length < 5 ||
            (!segments[^3].Equals("sektor", StringComparison.OrdinalIgnoreCase) &&
             !segments[^3].Equals("sektory", StringComparison.OrdinalIgnoreCase)) ||
            !segments[^1].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deviceAddress = "/" + string.Join('/', segments[..^3]);
        if (!SectorOscTargets.TryGetValue(deviceAddress, out var target))
        {
            return false;
        }

        int firstSector;
        int lastSector;
        if (segments[^3].Equals("sektor", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadNumber(segments[^2], target.MaxSector, out firstSector))
            {
                return true;
            }

            lastSector = firstSector;
        }
        else if (!TryReadInclusiveRange(segments[^2], target.MaxSector, out firstSector, out lastSector))
        {
            return true;
        }

        var sequence = new List<byte> { ModelProtocolConstants.ClearSectorSelection };
        for (var sector = firstSector; sector <= lastSector; sector++)
        {
            sequence.Add((byte)(ModelProtocolConstants.ToggleSector1 + sector - 1));
        }

        sequence.Add(target.OnCommand);
        commands = sequence;

        return true;
    }

    private static bool TryReadNumberedDeviceSequence(string[] segments, out IReadOnlyList<byte> commands)
    {
        commands = [];
        if (segments.Length is not (4 or 5) ||
            !segments[0].Equals("makieta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var (recognized, plural, target) = segments[1].ToLowerInvariant() switch
        {
            "wiatrak" => (true, false, new NumberedOscTarget(5, 0x04)),
            "wiatraki" => (true, true, new NumberedOscTarget(5, 0x04)),
            "balon" => (true, false, new NumberedOscTarget(3, 0x1A)),
            "balony" => (true, true, new NumberedOscTarget(3, 0x1A)),
            _ => (false, false, new NumberedOscTarget(0, 0x00))
        };
        if (!recognized)
        {
            return false;
        }

        int firstItem;
        int lastItem;
        if (plural)
        {
            if (!TryReadInclusiveRange(segments[2], target.MaxItem, out firstItem, out lastItem))
            {
                return true;
            }
        }
        else
        {
            if (!TryReadNumber(segments[2], target.MaxItem, out firstItem))
            {
                return true;
            }

            lastItem = firstItem;
        }

        if (segments.Length == 4 && segments[3].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            commands = Enumerable.Range(firstItem, lastItem - firstItem + 1)
                .Select(item => (byte)(target.FirstOffCommand + ((item - 1) * 2)))
                .ToArray();
            return true;
        }

        if (segments.Length == 5 &&
            segments[3].Equals("predkosc", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var percent) &&
            percent is >= 10 and <= 100 && percent % 10 == 0)
        {
            var sequence = new List<byte> { (byte)(0x0F + (percent / 10)) };
            for (var item = firstItem; item <= lastItem; item++)
            {
                sequence.Add((byte)(target.FirstOffCommand + ((item - 1) * 2) + 1));
            }

            commands = sequence;
        }

        return true;
    }

    private static bool TryReadNumber(string value, int maximum, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
        number >= 1 && number <= maximum;

    private static bool TryReadInclusiveRange(string value, int maximum, out int first, out int last)
    {
        first = 0;
        last = 0;
        var separator = value.IndexOf('-');
        if (separator <= 0 || separator != value.LastIndexOf('-'))
        {
            return false;
        }

        return TryReadNumber(value[..separator], maximum, out first) &&
               TryReadNumber(value[(separator + 1)..], maximum, out last) &&
               first < last;
    }

    private async Task HandleTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now >= _nextPingAt)
        {
            _nextPingAt = now + _options.MilluminPingInterval;
            await _osc.SendAsync(new OscMessage("/ping"), cancellationToken);
        }

        if (_milluminOnline && _lastMilluminMessageAt.HasValue && now - _lastMilluminMessageAt > _options.MilluminOfflineAfter)
        {
            _milluminOnline = false;
            _reconcileAt = null;
            _state = _state with
            {
                ObservedScenario = null,
                UpdatedAtUtc = now
            };
            await _stateStore.SaveAsync(_state, cancellationToken);
            _log.Warning("Millumin feedback timed out; waiting for reconnection.");
        }

        if (_milluminOnline &&
            _options.RestoreLastScenario &&
            _reconcileAt.HasValue &&
            now >= _reconcileAt.Value &&
            _reconciledEpoch != _milluminConnectionEpoch)
        {
            _reconciledEpoch = _milluminConnectionEpoch;
            _reconcileAt = null;
            if (_state.DesiredScenario is int desired && _state.ObservedScenario != desired &&
                _options.ScenarioColumns.TryGetValue(desired, out var target))
            {
                _log.Info($"Restoring persisted scenario {desired} after Millumin startup/reconnection.");
                await LaunchTargetAsync(desired, target, cancellationToken);
            }
        }

        if (_pendingModelCommand is not null && now >= _pendingModelCommand.NextAttemptAt)
        {
            await RetryPendingModelCommandAsync(now, cancellationToken, immediate: false);
        }

        if (_pendingModelCommand is null && _modelOutputQueue.TryDequeue(out var queuedData))
        {
            await StartModelCommandAsync(queuedData, now, cancellationToken);
        }
    }

    private void HandleSerialConnection(SerialConnectionEvent serialConnection)
    {
        _parser.Reset();
        _pendingModelCommand = null;
        _modelOutputQueue.Clear();
        if (serialConnection.Connected)
        {
            _log.Info($"Serial connected: {serialConnection.Port}.");
            QueueModelStatus(_state.ObservedScenario.HasValue
                ? ModelProtocolConstants.ScenarioRunning
                : ModelProtocolConstants.NoScenario);
        }
        else
        {
            _log.Warning("Serial disconnected; automatic reconnect is active.");
        }
    }

    private void QueueModelStatus(byte data)
    {
        if (_pendingModelCommand?.Data == data)
        {
            return;
        }

        if (_modelOutputQueue.Count == 0 || _modelOutputQueue.Last() != data)
        {
            _modelOutputQueue.Enqueue(data);
        }
    }

    private async Task StartModelCommandAsync(byte data, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!ModelProtocolConstants.TryCreateAcknowledgement(data, out var expectedAck))
        {
            return;
        }

        _pendingModelCommand = new PendingModelCommand(
            data,
            expectedAck,
            now,
            now + _options.ModelAckTimeout,
            1);
        await SendRawModelFrameAsync(data, cancellationToken);
    }

    private async Task RetryPendingModelCommandAsync(DateTimeOffset now, CancellationToken cancellationToken, bool immediate)
    {
        if (_pendingModelCommand is not { } pending)
        {
            return;
        }

        if (now - pending.StartedAt >= _options.ModelRetryWindow)
        {
            _log.Warning($"No valid ACK for model command 0x{pending.Data:X2} within {_options.ModelRetryWindow.TotalMilliseconds:0} ms.");
            _pendingModelCommand = null;
            return;
        }

        if (!immediate && now < pending.NextAttemptAt)
        {
            return;
        }

        var updated = pending with
        {
            NextAttemptAt = now + _options.ModelAckTimeout,
            Attempts = pending.Attempts + 1
        };
        _pendingModelCommand = updated;
        await SendRawModelFrameAsync(updated.Data, cancellationToken);
    }

    private async Task HandleModelAcknowledgementAsync(byte acknowledgement, byte acknowledgedData, CancellationToken cancellationToken)
    {
        if (_pendingModelCommand is { } pending && acknowledgement == pending.ExpectedAck)
        {
            _log.Debug($"Model ACK 0x{acknowledgement:X2} accepted for 0x{acknowledgedData:X2} after {pending.Attempts} attempt(s).");
            _pendingModelCommand = null;
            return;
        }

        if (_pendingModelCommand is not null)
        {
            _log.Warning($"Unexpected model ACK 0x{acknowledgement:X2}; expected 0x{_pendingModelCommand.ExpectedAck:X2}.");
            await SendRawModelFrameAsync(ModelProtocolConstants.AckFailed, cancellationToken);
        }
    }

    private async ValueTask SendRawModelFrameAsync(byte data, CancellationToken cancellationToken)
    {
        if (!_serial.IsConnected)
        {
            _log.Debug($"Serial TX 0x{data:X2} skipped while disconnected.");
            return;
        }

        try
        {
            await _serial.SendAsync(ModelFrameCodec.Encode(data), cancellationToken);
            _log.Debug($"Model TX 0x{data:X2}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _log.Warning($"Serial TX 0x{data:X2} failed: {exception.Message}");
        }
    }

    private int? FindScenario(int? index, string? name)
    {
        foreach (var pair in _options.ScenarioColumns)
        {
            if (pair.Value.Matches(index, name))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static (int? Index, string? Name) ReadColumnIdentity(OscMessage message)
    {
        int? index = message.Arguments.Count > 0 && message.Arguments[0] is int integer ? integer : null;
        string? name = message.Arguments.Count > 1 && message.Arguments[1] is string text ? text : null;
        return (index, name);
    }

    private abstract record BridgeEvent;
    private sealed record SerialBytesEvent(ReadOnlyMemory<byte> Bytes) : BridgeEvent;
    private sealed record ParsedFrameEvent(ModelFrame Frame) : BridgeEvent;
    private sealed record OscMessageEvent(OscMessage Message) : BridgeEvent;
    private sealed record SerialConnectionEvent(bool Connected, string? Port) : BridgeEvent;
    private sealed record TickEvent(DateTimeOffset Now) : BridgeEvent;
    private sealed record ManualModelCommandEvent(byte Data, string Source) : BridgeEvent;
    private sealed record PendingModelCommand(
        byte Data,
        byte ExpectedAck,
        DateTimeOffset StartedAt,
        DateTimeOffset NextAttemptAt,
        int Attempts);

    private sealed record SectorOscTarget(int MaxSector, byte OnCommand);
    private sealed record NumberedOscTarget(int MaxItem, byte FirstOffCommand);
}
