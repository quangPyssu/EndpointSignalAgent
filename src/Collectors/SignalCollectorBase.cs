using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;

namespace EndpointSignalAgent.Collectors;

public abstract class SignalCollectorBase : BackgroundService, IDisposable
{
    private readonly string _spoolPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected SignalCollectorBase(string spoolPath)
    {
        _spoolPath = spoolPath;
    }

    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var writer = new SpoolFileCollector(_spoolPath);
            await writer.WriteAsync(new SignalEvent(DateTimeOffset.UtcNow, type, payload));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public override void Dispose()
    {
        _writeLock.Dispose();
        base.Dispose();
    }
}
