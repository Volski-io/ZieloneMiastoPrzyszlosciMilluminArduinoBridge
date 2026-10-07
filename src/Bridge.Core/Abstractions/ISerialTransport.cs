namespace Bridge.Core.Abstractions;

public interface ISerialTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<ReadOnlyMemory<byte>>? BytesReceived;
    event Action<bool, string?>? ConnectionChanged;
    Task RunAsync(CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}
