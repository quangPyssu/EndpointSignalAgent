using EndpointSignalAgent.Contracts;

namespace EndpointSignalAgent.Providers;

public interface ISignalProvider
{
    ValueTask<IReadOnlyList<SignalEvent>> CollectAsync(CancellationToken ct);
}
