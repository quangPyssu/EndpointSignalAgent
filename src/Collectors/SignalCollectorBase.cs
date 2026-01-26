using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;
using System.Threading.Channels;

namespace EndpointSignalAgent.Collectors;

public abstract class SignalCollectorBase : BackgroundService
{
    private readonly string _spoolPath;
    private readonly ChannelWriter<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> _channelWriter;

    protected SignalCollectorBase(
        string spoolPath,
        ChannelWriter<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> channelWriter)
    {
        _spoolPath = spoolPath;
        _channelWriter = channelWriter;
    }

    protected async Task WriteSignalAsync(SignalEventType type, Dictionary<string, string> payload)
    {
        await _channelWriter.WriteAsync((type, payload, _spoolPath));
    }
}
