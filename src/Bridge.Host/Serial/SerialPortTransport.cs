using System.IO.Enumeration;
using System.IO.Ports;
using Bridge.Core.Abstractions;
using Bridge.Host.Configuration;

namespace Bridge.Host.Serial;

public sealed class SerialPortTransport(SerialOptions options, IBridgeLog log) : ISerialTransport
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialPort? _port;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _port?.IsOpen == true;
            }
        }
    }

    public event Action<ReadOnlyMemory<byte>>? BytesReceived;
    public event Action<bool, string?>? ConnectionChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            string? portName = null;
            try
            {
                portName = DiscoverPort();
                if (portName is null)
                {
                    log.Debug("No matching serial port found.");
                    await Task.Delay(options.ReconnectDelayMs, cancellationToken);
                    continue;
                }

                using var port = CreatePort(portName);
                port.Open();

                if (options.StartupDelayMs > 0)
                {
                    log.Debug($"Waiting {options.StartupDelayMs} ms for the serial adapter to become ready.");
                    await Task.Delay(options.StartupDelayMs, cancellationToken);
                }

                lock (_gate)
                {
                    _port = port;
                }

                ConnectionChanged?.Invoke(true, portName);
                await ReadLoopAsync(port, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Warning($"Serial connection failed{(portName is null ? string.Empty : $" on {portName}")}: {exception.Message}");
            }
            finally
            {
                SerialPort? disconnected;
                lock (_gate)
                {
                    disconnected = _port;
                    _port = null;
                }

                if (disconnected is not null)
                {
                    try
                    {
                        disconnected.Close();
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException)
                    {
                        log.Debug($"Ignoring serial close error: {exception.Message}");
                    }

                    ConnectionChanged?.Invoke(false, portName);
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.ReconnectDelayMs, cancellationToken);
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            SerialPort port;
            lock (_gate)
            {
                port = _port is { IsOpen: true }
                    ? _port
                    : throw new InvalidOperationException("Serial port is disconnected.");
            }

            await port.BaseStream.WriteAsync(bytes, cancellationToken);
            await port.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        lock (_gate)
        {
            _port?.Close();
            _port = null;
        }

        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (!cancellationToken.IsCancellationRequested && port.IsOpen)
        {
            var count = await port.BaseStream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                throw new IOException("Serial stream ended.");
            }

            BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray());
        }
    }

    private SerialPort CreatePort(string portName) => new(
        portName,
        options.BaudRate,
        options.Parity,
        options.DataBits,
        options.StopBits)
    {
        Handshake = options.Handshake,
        DtrEnable = options.DtrEnable,
        RtsEnable = options.RtsEnable,
        ReadTimeout = SerialPort.InfiniteTimeout,
        WriteTimeout = 1000
    };

    private string? DiscoverPort()
    {
        if (!string.IsNullOrWhiteSpace(options.PortName))
        {
            return options.PortName;
        }

        var candidates = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists("/dev"))
        {
            AddUnixCandidates(candidates, "cu.*");
            AddUnixCandidates(candidates, "tty.*");
        }

        var matching = candidates
            .Where(candidate => options.PortPatterns.Count == 0 || options.PortPatterns.Any(
                pattern => FileSystemName.MatchesSimpleExpression(pattern, candidate, ignoreCase: true)))
            .OrderBy(candidate => candidate.Contains("/cu.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matching.Length == 0)
        {
            return null;
        }

        if (matching.Length > 1 && options.RequireUniqueMatch)
        {
            throw new InvalidOperationException($"Multiple serial ports match: {string.Join(", ", matching)}. Set Serial.PortName explicitly.");
        }

        return matching[0];
    }

    private static void AddUnixCandidates(ISet<string> candidates, string pattern)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles("/dev", pattern))
            {
                candidates.Add(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
