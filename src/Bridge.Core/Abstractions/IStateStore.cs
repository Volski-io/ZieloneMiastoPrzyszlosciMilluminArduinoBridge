using Bridge.Core.Bridge;

namespace Bridge.Core.Abstractions;

public interface IStateStore
{
    ValueTask<BridgeState> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(BridgeState state, CancellationToken cancellationToken);
}
