namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class TemporalFeatureExtractor
{
    private readonly IReadOnlyList<ITemporalAggregator> _aggregators =
    [
        new TemporalMetadataAggregator(),
        new AppTemporalAggregator(),
        new SessionTemporalAggregator(),
        new NetworkTemporalAggregator(),
        new SystemTemporalAggregator(),
        new CrossTemporalAggregator()
    ];

    public Dictionary<string, double> Extract(TemporalWindowContext ctx)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var aggregator in _aggregators)
            foreach (var kv in aggregator.Aggregate(ctx))
                result[kv.Key] = kv.Value;
        return result;
    }
}
