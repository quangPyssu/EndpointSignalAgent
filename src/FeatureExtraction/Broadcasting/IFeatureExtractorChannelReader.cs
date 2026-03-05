using System.Threading.Channels;
using EndpointSignalAgent.SignalCollection.Broadcasting;

namespace EndpointSignalAgent.FeatureExtraction.Broadcasting;

/// <summary>
/// Marker interface for FeatureExtractorService channel reader.
/// </summary>
public interface IFeatureExtractorChannelReader
{
    ChannelReader<BroadcastSignal> Reader { get; }
}

internal sealed class FeatureExtractorChannelReader : IFeatureExtractorChannelReader
{
    public ChannelReader<BroadcastSignal> Reader { get; }

    public FeatureExtractorChannelReader(ChannelReader<BroadcastSignal> reader)
    {
        Reader = reader;
    }
}
