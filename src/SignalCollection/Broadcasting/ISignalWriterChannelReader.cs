using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Broadcasting;

/// <summary>
/// Marker interface for SignalWriterService channel reader.
/// </summary>
public interface ISignalWriterChannelReader
{
    ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> Reader { get; }
}

internal sealed class SignalWriterChannelReader : ISignalWriterChannelReader
{
    public ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> Reader { get; }

    public SignalWriterChannelReader(ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> reader)
    {
        Reader = reader;
    }
}
