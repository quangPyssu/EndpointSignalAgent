using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.SignalCollection.Broadcasting;

/// <summary>
/// Marker interface for SignalWriterService channel reader.
/// </summary>
public interface ISignalWriterChannelReader
{
    ChannelReader<BroadcastSignal> Reader { get; }
}

internal sealed class SignalWriterChannelReader : ISignalWriterChannelReader
{
    public ChannelReader<BroadcastSignal> Reader { get; }

    public SignalWriterChannelReader(ChannelReader<BroadcastSignal> reader)
    {
        Reader = reader;
    }
}
