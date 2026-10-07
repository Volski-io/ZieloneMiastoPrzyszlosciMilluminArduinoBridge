using System.Threading.Channels;
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
                }
            }
        }
        finally
        {
            await timerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
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
    private sealed record PendingModelCommand(
        byte Data,
        byte ExpectedAck,
        DateTimeOffset StartedAt,
        DateTimeOffset NextAttemptAt,
        int Attempts);
}
