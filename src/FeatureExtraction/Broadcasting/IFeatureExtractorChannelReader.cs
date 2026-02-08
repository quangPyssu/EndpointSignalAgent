using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.Broadcasting;

/// <summary>
/// Marker interface for FeatureExtractorService channel reader.
/// </summary>
public interface IFeatureExtractorChannelReader
{
    ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> Reader { get; }
}

internal sealed class FeatureExtractorChannelReader : IFeatureExtractorChannelReader
{
    public ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> Reader { get; }

    public FeatureExtractorChannelReader(ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> reader)
    {
        Reader = reader;
    }
}
