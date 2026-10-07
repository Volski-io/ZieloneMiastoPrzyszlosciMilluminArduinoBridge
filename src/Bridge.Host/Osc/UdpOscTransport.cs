using System.Net;
using System.Net.Sockets;
using Bridge.Core.Abstractions;
using Bridge.Core.Osc;
using Bridge.Host.Configuration;
using Bridge.Host.Telemetry;

namespace Bridge.Host.Osc;

public sealed class UdpOscTransport(MilluminOptions options, IBridgeLog log, TrafficJournal? traffic = null) : IOscTransport
{
    private readonly UdpClient _sender = new();
    private UdpClient? _receiver;
    private IPEndPoint? _remoteEndpoint;

    public event Action<OscMessage>? MessageReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _remoteEndpoint = await ResolveEndpointAsync(options.Host, options.InputPort, cancellationToken);
        _receiver = new UdpClient(new IPEndPoint(IPAddress.Any, options.FeedbackPort));
        log.Info($"OSC feedback listener active on UDP {options.FeedbackPort}; Millumin target is {_remoteEndpoint}.");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _receiver.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var message in OscPacketCodec.Decode(result.Buffer))
            {
                log.Debug($"OSC RX {message.Address} [{string.Join(", ", message.Arguments)}].");
                traffic?.RecordOsc("RX", message, result.Buffer);
                MessageReceived?.Invoke(message);
            }
        }
    }

    public async ValueTask SendAsync(OscMessage message, CancellationToken cancellationToken)
    {
        var endpoint = _remoteEndpoint ??= await ResolveEndpointAsync(options.Host, options.InputPort, cancellationToken);
        var packet = OscPacketCodec.Encode(message);
        await _sender.SendAsync(packet, endpoint, cancellationToken);
        traffic?.RecordOsc("TX", message, packet);
        log.Debug($"OSC TX {message.Address} [{string.Join(", ", message.Arguments)}].");
    }

    public ValueTask DisposeAsync()
    {
        _receiver?.Dispose();
        _sender.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new IPEndPoint(address, port);
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var selected = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Cannot resolve OSC host '{host}'.");
        return new IPEndPoint(selected, port);
    }
}
