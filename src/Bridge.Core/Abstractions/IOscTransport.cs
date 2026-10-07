using Bridge.Core.Osc;

namespace Bridge.Core.Abstractions;

public interface IOscTransport : IAsyncDisposable
{
    event Action<OscMessage>? MessageReceived;
    Task RunAsync(CancellationToken cancellationToken);
    ValueTask SendAsync(OscMessage message, CancellationToken cancellationToken);
}
