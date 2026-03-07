using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Hosting;

namespace EndpointSignalAgent.SignalCollection.Collectors;

public abstract class SignalCollectorBase : BackgroundService
{
    private readonly string _spoolPath;
    private readonly ISignalBroadcaster _broadcaster;
    private readonly ICollectionControl _collectionControl;

    protected SignalCollectorBase(
        string spoolPath,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl)
    {
        _spoolPath = spoolPath;
        _broadcaster = broadcaster;
        _collectionControl = collectionControl;
    }

    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        if (_collectionControl.IsPaused)
        {
            return;
        }

        await _broadcaster.BroadcastAsync(type, payload, _spoolPath);
    }
}
