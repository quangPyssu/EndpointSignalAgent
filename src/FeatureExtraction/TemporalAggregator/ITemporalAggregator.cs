namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal interface ITemporalAggregator
{
    IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx);
}
