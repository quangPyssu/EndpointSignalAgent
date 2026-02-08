using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using Microsoft.Extensions.Hosting;

namespace EndpointSignalAgent.SignalCollection.Collectors;

public abstract class SignalCollectorBase : BackgroundService
{
    private readonly string _spoolPath;
    private readonly ISignalBroadcaster _broadcaster;

    protected SignalCollectorBase(
        string spoolPath,
        ISignalBroadcaster broadcaster)
    {
        _spoolPath = spoolPath;
        _broadcaster = broadcaster;
    }

    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        await _broadcaster.BroadcastAsync(type, payload, _spoolPath);
    }
}
