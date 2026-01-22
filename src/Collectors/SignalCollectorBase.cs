using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;
using System.Threading.Channels;

namespace EndpointSignalAgent.Collectors;

public abstract class SignalCollectorBase : BackgroundService, IDisposable
{
    private readonly string _spoolPath;
    private static readonly Channel<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> _signalChannel = 
        Channel.CreateBounded<(SignalEventType, Dictionary<string, string>, string)>(new BoundedChannelOptions(1000)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static readonly Task _writerTask;

    static SignalCollectorBase()
    {
        _writerTask = Task.Run(async () =>
        {
            await foreach (var signal in _signalChannel.Reader.ReadAllAsync())
            {
                try
                {
                    using var writer = new SpoolFileCollector(signal.SpoolPath);
                    await writer.WriteAsync(new SignalEvent(DateTimeOffset.UtcNow, signal.Type, new Dictionary<string, string>(signal.Payload)));
                }
                catch
                {
                    // Silent fail to prevent writer task from crashing
                }
            }
        });
    }

    protected SignalCollectorBase(string spoolPath)
    {
        _spoolPath = spoolPath;
    }

    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        await _signalChannel.Writer.WriteAsync((type, payload, _spoolPath));
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
