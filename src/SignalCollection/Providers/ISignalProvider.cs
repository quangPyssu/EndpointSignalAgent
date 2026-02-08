using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Providers;

public interface ISignalProvider
{
    ValueTask<IReadOnlyList<SignalEvent>> CollectAsync(CancellationToken ct);
}
